﻿// ==========================================================================
//  Squidex Headless CMS
// ==========================================================================
//  Copyright (c) Squidex UG (haftungsbeschraenkt)
//  All rights reserved. Licensed under the MIT license.
// ==========================================================================

using Microsoft.Extensions.Options;
using Squidex.Domain.Apps.Entities.Assets.Repositories;
using Squidex.Infrastructure;

namespace Squidex.Domain.Apps.Entities.Assets.Queries;

public sealed class AssetQueryService : IAssetQueryService
{
    private readonly IAssetEnricher assetEnricher;
    private readonly IAssetRepository assetRepository;
    private readonly IAssetLoader assetLoader;
    private readonly IAssetFolderRepository assetFolderRepository;
    private readonly AssetOptions options;
    private readonly AssetQueryParser queryParser;

    public AssetQueryService(
        IAssetEnricher assetEnricher,
        IAssetRepository assetRepository,
        IAssetLoader assetLoader,
        IAssetFolderRepository assetFolderRepository,
        IOptions<AssetOptions> options,
        AssetQueryParser queryParser)
    {
        this.assetEnricher = assetEnricher;
        this.assetRepository = assetRepository;
        this.assetLoader = assetLoader;
        this.assetFolderRepository = assetFolderRepository;
        this.options = options.Value;
        this.queryParser = queryParser;
    }

    public async Task<IReadOnlyList<IAssetFolderEntity>> FindAssetFolderAsync(DomainId appId, DomainId id,
        CancellationToken ct = default)
    {
        using (var activity = Telemetry.Activities.StartActivity("AssetQueryService/FindAssetFolderAsync"))
        {
            activity?.SetTag("assetId", id);

            var result = new List<IAssetFolderEntity>();

            while (id != DomainId.Empty)
            {
                var folder = await FindFolderCoreAsync(appId, id, ct);

                if (folder == null || result.Exists(x => x.Id == folder.Id))
                {
                    result.Clear();
                    break;
                }

                result.Insert(0, folder);

                id = folder.ParentId;
            }

            return result;
        }
    }

    public async Task<IResultList<IAssetFolderEntity>> QueryAssetFoldersAsync(Context context, DomainId? parentId,
        CancellationToken ct = default)
    {
        using (var activity = Telemetry.Activities.StartActivity("AssetQueryService/QueryAssetFoldersAsync"))
        {
            activity?.SetTag("folderId", parentId);

            var assetFolders = await QueryFoldersCoreAsync(context, parentId, ct);

            return assetFolders;
        }
    }

    public async Task<IEnrichedAssetEntity?> FindByHashAsync(Context context, string hash, string fileName, long fileSize,
        CancellationToken ct = default)
    {
        Guard.NotNull(context);

        using (var activity = Telemetry.Activities.StartActivity("AssetQueryService/FindByHashAsync"))
        {
            activity?.SetTag("fileHash", hash);
            activity?.SetTag("fileName", fileName);
            activity?.SetTag("fileSize", fileSize);

            var asset = await FindByHashCoreAsync(context, hash, fileName, fileSize, ct);

            if (asset == null)
            {
                return null;
            }

            return await TransformAsync(context, asset, ct);
        }
    }

    public async Task<IEnrichedAssetEntity?> FindBySlugAsync(Context context, string slug, bool allowDeleted = false,
        CancellationToken ct = default)
    {
        Guard.NotNull(context);

        using (var activity = Telemetry.Activities.StartActivity("AssetQueryService/FindBySlugAsync"))
        {
            activity?.SetTag("slug", slug);

            var asset = await FindBySlugCoreAsync(context, slug, allowDeleted, ct);

            if (asset == null)
            {
                return null;
            }

            return await TransformAsync(context, asset, ct);
        }
    }

    public async Task<IEnrichedAssetEntity?> FindGlobalAsync(Context context, DomainId id,
        CancellationToken ct = default)
    {
        Guard.NotNull(context);

        using (var activity = Telemetry.Activities.StartActivity("AssetQueryService/FindGlobalAsync"))
        {
            activity?.SetTag("assetId", id);

            var asset = await FindCoreAsync(id, ct);

            if (asset == null)
            {
                return null;
            }

            return await TransformAsync(context, asset, ct);
        }
    }

    public async Task<IEnrichedAssetEntity?> FindAsync(Context context, DomainId id, bool allowDeleted = false, long version = EtagVersion.Any,
        CancellationToken ct = default)
    {
        Guard.NotNull(context);

        using (var activity = Telemetry.Activities.StartActivity("AssetQueryService/FindAsync"))
        {
            activity?.SetTag("assetId", id);

            IAssetEntity? asset;

            if (version > EtagVersion.Empty)
            {
                asset = await assetLoader.GetAsync(context.App.Id, id, version, ct);
            }
            else
            {
                asset = await FindCoreAsync(context, id, allowDeleted, ct);
            }

            if (asset == null)
            {
                return null;
            }

            return await TransformAsync(context, asset, ct);
        }
    }

    public async Task<IResultList<IEnrichedAssetEntity>> QueryAsync(Context context, DomainId? parentId, Q q,
        CancellationToken ct = default)
    {
        Guard.NotNull(context);

        if (q == null)
        {
            return ResultList.Empty<IEnrichedAssetEntity>();
        }

        using (Telemetry.Activities.StartActivity("AssetQueryService/QueryAsync"))
        {
            q = await ParseCoreAsync(context, q, ct);

            var assets = await QueryCoreAsync(context, parentId, q, ct);

            if (q.Ids is { Count: > 0 })
            {
                assets = assets.Sorted(x => x.Id, q.Ids);
            }

            return await TransformAsync(context, assets, ct);
        }
    }

    private async Task<IResultList<IEnrichedAssetEntity>> TransformAsync(Context context, IResultList<IAssetEntity> assets,
        CancellationToken ct)
    {
        var transformed = await TransformCoreAsync(context, assets, ct);

        return ResultList.Create(assets.Total, transformed);
    }

    private async Task<IEnrichedAssetEntity> TransformAsync(Context context, IAssetEntity asset,
        CancellationToken ct)
    {
        var transformed = await TransformCoreAsync(context, Enumerable.Repeat(asset, 1), ct);

        return transformed[0];
    }

    private async Task<IReadOnlyList<IEnrichedAssetEntity>> TransformCoreAsync(Context context, IEnumerable<IAssetEntity> assets,
        CancellationToken ct)
    {
        using (Telemetry.Activities.StartActivity("AssetQueryService/TransformCoreAsync"))
        {
            return await assetEnricher.EnrichAsync(assets, context, ct);
        }
    }

    private async Task<Q> ParseCoreAsync(Context context, Q q,
        CancellationToken ct)
    {
        using (var combined = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            // Enforce a hard timeout
            combined.CancelAfter(options.TimeoutQuery);

            return await queryParser.ParseAsync(context, q, ct);
        }
    }

    private async Task<IResultList<IAssetFolderEntity>> QueryFoldersCoreAsync(Context context, DomainId? parentId,
        CancellationToken ct)
    {
        using (var combined = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            // Enforce a hard timeout
            combined.CancelAfter(options.TimeoutQuery);

            return await assetFolderRepository.QueryAsync(context.App.Id, parentId, combined.Token);
        }
    }

    private async Task<IResultList<IAssetEntity>> QueryCoreAsync(Context context, DomainId? parentId, Q q,
        CancellationToken ct)
    {
        using (var combined = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            // Enforce a hard timeout
            combined.CancelAfter(options.TimeoutQuery);

            return await assetRepository.QueryAsync(context.App.Id, parentId, q, combined.Token);
        }
    }

    private async Task<IAssetFolderEntity?> FindFolderCoreAsync(DomainId appId, DomainId id,
        CancellationToken ct)
    {
        using (var combined = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            // Enforce a hard timeout
            combined.CancelAfter(options.TimeoutFind);

            return await assetFolderRepository.FindAssetFolderAsync(appId, id, combined.Token);
        }
    }

    private async Task<IAssetEntity?> FindByHashCoreAsync(Context context, string hash, string fileName, long fileSize,
        CancellationToken ct)
    {
        using (var combined = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            // Enforce a hard timeout
            combined.CancelAfter(options.TimeoutFind);

            return await assetRepository.FindAssetByHashAsync(context.App.Id, hash, fileName, fileSize, combined.Token);
        }
    }

    private async Task<IAssetEntity?> FindBySlugCoreAsync(Context context, string slug, bool allowDeleted,
        CancellationToken ct)
    {
        using (var combined = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            // Enforce a hard timeout
            combined.CancelAfter(options.TimeoutFind);

            return await assetRepository.FindAssetBySlugAsync(context.App.Id, slug, allowDeleted, combined.Token);
        }
    }

    private async Task<IAssetEntity?> FindCoreAsync(DomainId id,
        CancellationToken ct)
    {
        using (var combined = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            // Enforce a hard timeout
            combined.CancelAfter(options.TimeoutFind);

            return await assetRepository.FindAssetAsync(id, combined.Token);
        }
    }

    private async Task<IAssetEntity?> FindCoreAsync(Context context, DomainId id, bool allowDeleted,
        CancellationToken ct)
    {
        using (var combined = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            // Enforce a hard timeout
            combined.CancelAfter(options.TimeoutFind);

            return await assetRepository.FindAssetAsync(context.App.Id, id, allowDeleted, combined.Token);
        }
    }
}
