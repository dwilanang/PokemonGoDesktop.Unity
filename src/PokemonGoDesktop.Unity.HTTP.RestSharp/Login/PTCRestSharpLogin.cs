﻿using Easyception;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PokemonGoDesktop.API.Client.Services;
using PokemonGoDesktop.API.Common;
using RestSharp;
using RestSharp.Extensions.MonoHttp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace PokemonGoDesktop.Unity.HTTP.RestSharp
{
	//TODO: Refactor class
	//TODO: Make login async
	/// <summary>
	/// Pokemon Trainer Club login implementation of the <see cref="IUserAuthenticationService"/>.
	/// </summary>
	public class PTCRestSharpLogin : IUserAuthenticationService
	{
		/// <summary>
		/// Auth Type.
		/// </summary>
		private AuthType authenticationType { get; } = AuthType.PTC;

		/// <summary>
		/// Pokemon Trainer Login URL.
		/// </summary>
		public string ptcLoginUrl { get; }

		/// <summary>
		/// User password.
		/// </summary>
		private string userPassword { get; }

		/// <summary>
		/// User login name.
		/// </summary>
		public string userLoginName { get; }

		/// <summary>
		/// OAUth Url.
		/// </summary>
		public string loginRequestOAuthTokenUrl { get; }

		public PTCRestSharpLogin(string loginUrl, string loginOAuthUrl, string username, string password)
		{
			Throw<ArgumentNullException>.If.IsNull(loginUrl)?.Now(nameof(loginUrl), $"The {nameof(PTCRestSharpLogin)} service requires a non-null login url.");
			Throw<ArgumentNullException>.If.IsNull(username)?.Now(nameof(username), $"The {nameof(PTCRestSharpLogin)} service requires a non-null userLoginName.");
			Throw<ArgumentNullException>.If.IsNull(loginOAuthUrl)?.Now(nameof(loginOAuthUrl), $"The {nameof(PTCRestSharpLogin)} service requires a non-null loginOAuthUrl.");
			Throw<ArgumentNullException>.If.IsNull(password)?.Now(nameof(password), $"The {nameof(PTCRestSharpLogin)} service requires a non-null password.");

			ptcLoginUrl = loginUrl;
			loginRequestOAuthTokenUrl = loginOAuthUrl;

			userLoginName = username;
			userPassword = password;
		}

		/// <summary>
		/// Tries to get the login session cookie the PTC servers issue on gets.
		/// </summary>
		/// <param name="client">Cookie enabled client.</param>
		/// <returns>A <see cref="PTCLoginSessionCookie"/> instance or throws on failure.</returns>
		private PTCLoginSessionCookie TryGetLoginSessionCookie(RestClient client)
		{
			RestRequest request = new RestRequest();

			//We should do a blocking call for now until we can setup async for login
			IRestResponse sessionResponse = client.Get(request); //send an empty get

			string getContent = sessionResponse.Content.ToString();

			PTCLoginSessionCookie cookie;
			try
			{
				//We try to get the cookie deserialized
				cookie = JsonConvert.DeserializeObject<PTCLoginSessionCookie>(getContent);
			}
			catch (Exception e)
			{
				throw new InvalidOperationException("Failed to deserialize JSON login response. This could be because the servers are offline.", e);
			}

			if (cookie == null || String.IsNullOrEmpty(cookie.ExecutionID) || String.IsNullOrEmpty(cookie.LT))
				throw new InvalidOperationException($"{cookie} of Type {nameof(PTCLoginSessionCookie)} is null HTTPContent: {getContent}.");

			//This means there are error messages in the initial cookie; I don't know why this would happen
			if (!cookie.isValid)
				throw new InvalidOperationException($"Recieved errors in initial cookie exchange {cookie.ErrorStrings.Aggregate("", (e1, e2) => $"{e1} {e2}")}");

			return cookie;
		}

		/// <summary>
		/// Generates a cookie accepting <see cref="RestClient"/> that has base headers
		/// and a Ninantic user-agent.
		/// </summary>
		/// <returns>A non-null <see cref="RestClient"/>.</returns>
		private RestClient BuildLoginRestClient()
		{
			RestClient client = new RestClient(this.ptcLoginUrl);
			client.ClearHandlers();
			client.CookieContainer = new CookieContainer(); //https://github.com/restsharp/RestSharp/wiki/Cookies
			client.UserAgent = "Ninantic App";
			//client.AddDefaultHeader("Accept-Encoding", "gzip");

			//Let's hope RestClient can decompress gzip
			client.FollowRedirects = false; //Rocket-API disables redirection

			return client;
		}

		/// <summary>
		/// Attempts to get the OAuth Ticket ID from the login attempt.
		/// </summary>
		/// <param name="client">Restclient to use.</param>
		/// <returns>The OAuth ticket if available or null.</returns>
		private string TryGetTicketId(RestClient client)
		{
			PTCLoginSessionCookie cookie = TryGetLoginSessionCookie(client);

			//Build the login request
			//lt=LTVALUE&_eventId=submit&username=USERNAMEy&password=PASSWORD&Login=Sign+In
			RestRequest request = new RestRequest();
			request.AddParameter("application/x-www-form-urlencoded", $"lt={cookie.LT}&execution={cookie.ExecutionID}&_eventId={"submit"}&username={this.userLoginName}&password={this.userPassword}", ParameterType.RequestBody);

			//Wrap this block in a continue expected. Only way to do it with rest sharp

			IRestResponse loginResponse = client.Post(request);

			Parameter locationHeader = loginResponse.Headers.FirstOrDefault(h => h.Name == "Location");

			if (locationHeader == null)
			{
				HandleNullLocationHeader(loginResponse);
			}		

			string ticketId = null;

			try
			{
				//Like Rocket-API we must get the ticket id
				//WARNING: You must conver to Uri so we can generate the query
				ticketId = HttpUtility.ParseQueryString(new Uri(locationHeader.Value.ToString()).Query).Get("ticket");
			}
			catch(Exception e)
			{
				throw new InvalidOperationException("Unable to get Ticket ID. Could be failed login or login to offline servers.", e);
			}

			if (ticketId == null)
				throw new InvalidOperationException($"Unable to get Ticket ID. Could be failed login or login to offline servers. Location Value: {locationHeader.Value.ToString()} Content: {loginResponse.Content.ToString()}");

			return ticketId;
		}

		/// <summary>
		/// Handles cases when the location header in the login response is null.
		/// </summary>
		/// <param name="loginResponse">Login response with the null location header.</param>
		private void HandleNullLocationHeader(IRestResponse loginResponse)
		{
			if (loginResponse.Content != null && loginResponse.Content.Length != 0)
			{
				//try to get the cookie
				PTCLoginSessionCookie errorCookie = null;
				try
				{
					errorCookie = JsonConvert.DeserializeObject<PTCLoginSessionCookie>(loginResponse.Content.ToString());
				}
				catch (Exception e)
				{

				}

				//If there is no location header it's likely that there is an error message in the form of a JSON object, the cookie, sent back to us.
				if (errorCookie != null && !errorCookie.isValid)
					throw new ServerLoginException($"PTC Error: {errorCookie.ErrorStrings.Aggregate("", (e1, e2) => $"{e1} {e2}")}.");
			}

			throw new InvalidOperationException("Error: Failed to parse Location Header from login response.");
		}

		/// <summary>
		/// Attempts to authenticate the user.
		/// </summary>
		/// <returns>Resulting authentication token from the attempt.</returns>
		public IAuthToken TryAuthenticate()
		{
			RestClient client = BuildLoginRestClient();
			string ticketId = TryGetTicketId(client);

			//RestClient client = new RestClient(loginRequestOAuthTokenUrl);

			client.BaseUrl = new Uri(loginRequestOAuthTokenUrl);

			//We should do a blocking call for now until we can setup async for login

			RestRequest request = new RestRequest();

			//client_id={client_id}&redirect_uri={redirect_uri}&client_secret={client_secret}&grant_type={grant_type}&code={code}
			request.AddParameter("application/x-www-form-urlencoded", $"client_id={"mobile-app_pokemon-go"}&redirect_uri={@"https://www.nianticlabs.com/pokemongo/error"}&client_secret={"w8ScCUXJQc6kXKw8FiOhd8Fixzht18Dq3PEVkUCP5ZPxtgyWsbTvWHFLm2wNY0JR"}&grant_type={"refresh_token"}&code={ticketId}", ParameterType.RequestBody);

			IRestResponse oAuthTokenResponse = client.Post(request);	

			string accessToken = null;

			try
			{
				string tokenData = oAuthTokenResponse.Content;
				accessToken = HttpUtility.ParseQueryString(tokenData)["access_token"];
			}
			catch(Exception e)
			{
#if DEBUG || DEBUGBUILD
				throw new InvalidOperationException($"Error: {e.Message} Failed to generate access token from oAuth server.");
#else
				//Something went wrong. Return an invalid token
				return new AuthToken(authenticationType, false, null);
#endif
			}

			if (accessToken == null)
				throw new InvalidOperationException("Error: oAuth access token is invalid.");

			return new AuthToken(authenticationType, true, accessToken);
		}
	}
}
