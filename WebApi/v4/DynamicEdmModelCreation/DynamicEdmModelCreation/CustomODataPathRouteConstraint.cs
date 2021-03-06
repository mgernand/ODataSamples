﻿// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License.  See License.txt in the project root for license information.

namespace DynamicEdmModelCreation
{
	using System;
	using System.Collections.Generic;
	using System.Net;
	using System.Net.Http;
	using System.Web.Http;
	using System.Web.Http.Routing;
	using System.Web.OData.Extensions;
	using System.Web.OData.Routing;
	using Microsoft.OData.Edm;
	using Microsoft.Extensions.DependencyInjection;
	using Microsoft.OData;

	// based on: https://github.com/OData/WebApi/blob/master/src/System.Web.OData/OData/Routing/ODataPathRouteConstraint.cs
	public class CustomODataPathRouteConstraint : ODataPathRouteConstraint
	{
		// "%2F"
		private static readonly string escapedSlash = Uri.HexEscape('/');

		public CustomODataPathRouteConstraint(string routeName)
			: base(routeName)
		{
		}

		public override bool Match(HttpRequestMessage request, IHttpRoute route, string parameterName, IDictionary<string, object> values, HttpRouteDirection routeDirection)
		{
			if (request == null)
			{
				throw new ArgumentNullException(nameof(request));
			}

			if (values == null)
			{
				throw new ArgumentNullException(nameof(values));
			}

			if (routeDirection == HttpRouteDirection.UriResolution)
			{
				if (values.TryGetValue(ODataRouteConstants.ODataPath, out object oDataPathValue))
				{
					string oDataPathString = oDataPathValue as string;
					ODataPath path;

					try
					{
						IServiceProvider requestContainer = request.CreateRequestContainer(this.RouteName);
						IHttpRequestMessageProvider httpRequestMessageProvider = requestContainer.GetRequiredService<IHttpRequestMessageProvider>();
						httpRequestMessageProvider.Request = request;

						string[] segments = oDataPathString.Split('/');
						string dataSource = segments[0];
						request.Properties[Constants.ODataDataSource] = dataSource;
						request.Properties[Constants.CustomODataPath] = string.Join("/", segments, 1, segments.Length - 1);
						oDataPathString = (string)request.Properties[Constants.CustomODataPath];

						// Service root is the current RequestUri, less the query string and the ODataPath (always the
						// last portion of the absolute path).  ODL expects an escaped service root and other service
						// root calculations are calculated using AbsoluteUri (also escaped).  But routing exclusively
						// uses unescaped strings, determined using
						//    address.GetComponents(UriComponents.Path, UriFormat.Unescaped)
						//
						// For example if the AbsoluteUri is
						// <http://localhost/odata/FunctionCall(p0='Chinese%E8%A5%BF%E9%9B%85%E5%9B%BEChars')>, the
						// oDataPathString will contain "FunctionCall(p0='Chinese西雅图Chars')".
						//
						// Due to this decoding and the possibility of unecessarily-escaped characters, there's no
						// reliable way to determine the original string from which oDataPathString was derived.
						// Therefore a straightforward string comparison won't always work.  See RemoveODataPath() for
						// details of chosen approach.
						string requestLeftPart = request.RequestUri.GetLeftPart(UriPartial.Path);
						string serviceRoot = requestLeftPart;
						if (!string.IsNullOrEmpty(oDataPathString))
						{
							serviceRoot = RemoveODataPath(serviceRoot, oDataPathString);
						}

						// As mentioned above, we also need escaped ODataPath.
						// The requestLeftPart and request.RequestUri.Query are both escaped.
						// The ODataPath for service documents is empty.
						string oDataPathAndQuery = requestLeftPart.Substring(serviceRoot.Length);
						if (!string.IsNullOrEmpty(request.RequestUri.Query))
						{
							// Ensure path handler receives the query string as well as the path.
							oDataPathAndQuery += request.RequestUri.Query;
						}

						// Leave an escaped '/' out of the service route because DefaultODataPathHandler will add a
						// literal '/' to the end of this string if not already present. That would double the slash
						// in response links and potentially lead to later 404s.
						if (serviceRoot.EndsWith(escapedSlash, StringComparison.OrdinalIgnoreCase))
						{
							serviceRoot = serviceRoot.Substring(0, serviceRoot.Length - 3);
						}

						IODataPathHandler pathHandler = requestContainer.GetRequiredService<IODataPathHandler>();
						path = pathHandler.Parse(serviceRoot, oDataPathAndQuery, requestContainer);
					}
					catch (ODataException)
					{
						path = null;
					}

					if (path != null)
					{
						// Set all the properties we need for routing, querying, formatting
						request.ODataProperties().Path = path;
						request.ODataProperties().RouteName = this.RouteName;

						if (!values.ContainsKey(ODataRouteConstants.Controller))
						{
							// Select controller name using the routing conventions
							string controllerName = this.SelectControllerName(path, request);
							if (controllerName != null)
							{
								values[ODataRouteConstants.Controller] = controllerName;
							}
						}

						return true;
					}
				}

				// The request doesn't match this route so dipose the request container.
				request.DeleteRequestContainer(true);
				return false;
			}
			else
			{
				// This constraint only applies to URI resolution
				return true;
			}
		}

		// Find the substring of the given URI string before the given ODataPath.  Tests rely on the following:
		// 1. ODataPath comes at the end of the processed Path
		// 2. Virtual path root, if any, comes at the beginning of the Path and a '/' separates it from the rest
		// 3. OData prefix, if any, comes between the virtual path root and the ODataPath and '/' characters separate
		//    it from the rest
		// 4. Even in the case of Unicode character corrections, the only differences between the escaped Path and the
		//    unescaped string used for routing are %-escape sequences which may be present in the Path
		//
		// Therefore, look for the '/' character at which to lop off the ODataPath.  Can't just unescape the given
		// uriString because subsequent comparisons would only help to check wehther a match is _possible_, not where
		// to do the lopping.
		private static string RemoveODataPath(string uriString, string oDataPathString)
		{
			// Potential index of oDataPathString within uriString.
			int endIndex = uriString.Length - oDataPathString.Length - 1;
			if (endIndex <= 0)
			{
				// Bizarre: oDataPathString is longer than uriString.  Likely the values collection passed to Match()
				// is corrupt.
				throw new InvalidOperationException($"Request Uri Is Too Short For ODataPath. the Uri is {uriString}, and the OData path is {oDataPathString}.");
			}

			string startString = uriString.Substring(0, endIndex + 1);  // Potential return value.
			string endString = uriString.Substring(endIndex + 1);       // Potential oDataPathString match.
			if (string.Equals(endString, oDataPathString, StringComparison.Ordinal))
			{
				// Simple case, no escaping in the ODataPathString portion of the Path.  In this case, don't do extra
				// work to look for trailing '/' in startString.
				return startString;
			}

			while (true)
			{
				// Escaped '/' is a derivative case but certainly possible.
				int slashIndex = startString.LastIndexOf('/', endIndex - 1);
				int escapedSlashIndex =
					startString.LastIndexOf(escapedSlash, endIndex - 1, StringComparison.OrdinalIgnoreCase);
				if (slashIndex > escapedSlashIndex)
				{
					endIndex = slashIndex;
				}
				else if (escapedSlashIndex >= 0)
				{
					// Include the escaped '/' (three characters) in the potential return value.
					endIndex = escapedSlashIndex + 2;
				}
				else
				{
					// Failure, unable to find the expected '/' or escaped '/' separator.
					throw new InvalidOperationException($"The OData path is not found. The Uri is {uriString}, and the OData path is {oDataPathString}.");
				}

				startString = uriString.Substring(0, endIndex + 1);
				endString = uriString.Substring(endIndex + 1);

				// Compare unescaped strings to avoid both arbitrary escaping and use of lowercase 'a' through 'f' in
				// %-escape sequences.
				endString = Uri.UnescapeDataString(endString);
				if (string.Equals(endString, oDataPathString, StringComparison.Ordinal))
				{
					return startString;
				}

				if (endIndex == 0)
				{
					// Failure, could not match oDataPathString after an initial '/' or escaped '/'.
					throw new InvalidOperationException($"The OData path is not found. The Uri is {uriString}, and the OData path is {oDataPathString}.");
				}
			}
		}
	}
}