﻿// ==========================================================================
//  Squidex Headless CMS
// ==========================================================================
//  Copyright (c) Squidex UG (haftungsbeschraenkt)
//  All rights reserved. Licensed under the MIT license.
// ==========================================================================

using System.Collections.Immutable;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Squidex.Areas.IdentityServer.Config;

public sealed class AlwaysAddScopeHandler : IOpenIddictServerHandler<ProcessSignInContext>
{
    public ValueTask HandleAsync(ProcessSignInContext context)
    {
        if (context == null)
        {
            return default;
        }

        if (!string.IsNullOrWhiteSpace(context.Response.AccessToken))
        {
            var scopes = context.AccessTokenPrincipal?.GetScopes() ?? [];

            context.Response.Scope = string.Join(' ', scopes);
        }

        return default;
    }
}
