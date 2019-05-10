﻿// ==========================================================================
//  Squidex Headless CMS
// ==========================================================================
//  Copyright (c) Squidex UG (haftungsbeschraenkt)
//  All rights reserved. Licensed under the MIT license.
// ==========================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.OData;
using Squidex.Domain.Apps.Core.Tags;
using Squidex.Domain.Apps.Entities.Assets.Edm;
using Squidex.Domain.Apps.Entities.Assets.Queries;
using Squidex.Domain.Apps.Entities.Assets.Repositories;
using Squidex.Infrastructure;
using Squidex.Infrastructure.Queries;
using Squidex.Infrastructure.Queries.OData;

namespace Squidex.Domain.Apps.Entities.Assets
{
    public class AssetQueryService : IAssetQueryService
    {
        private readonly ITagService tagService;
        private readonly IAssetRepository assetRepository;
        private readonly AssetOptions options;

        public int DefaultPageSize
        {
            get { return options.DefaultPageSize; }
        }

        public AssetQueryService(ITagService tagService, IAssetRepository assetRepository, IOptions<AssetOptions> options)
        {
            Guard.NotNull(tagService, nameof(tagService));
            Guard.NotNull(options, nameof(options));
            Guard.NotNull(assetRepository, nameof(assetRepository));

            this.assetRepository = assetRepository;
            this.options = options.Value;
            this.tagService = tagService;
        }

        public Task<IAssetEntity> FindAssetAsync(QueryContext context, Guid id)
        {
            Guard.NotNull(context, nameof(context));

            return FindAssetAsync(context.App.Id, id);
        }

        public async Task<IAssetEntity> FindAssetAsync(Guid appId, Guid id)
        {
            var asset = await assetRepository.FindAssetAsync(id);

            if (asset != null)
            {
                await DenormalizeTagsAsync(appId, Enumerable.Repeat(asset, 1));
            }

            return asset;
        }

        public async Task<IList<IAssetEntity>> QueryByHashAsync(Guid appId, string hash)
        {
            Guard.NotNull(hash, nameof(hash));

            var assets = await assetRepository.QueryByHashAsync(appId, hash);

            await DenormalizeTagsAsync(appId, assets);

            return assets;
        }

        public async Task<IResultList<IAssetEntity>> QueryAsync(QueryContext context, Q query)
        {
            Guard.NotNull(context, nameof(context));
            Guard.NotNull(query, nameof(query));

            IResultList<IAssetEntity> assets;

            if (query.Ids != null)
            {
                assets = await assetRepository.QueryAsync(context.App.Id, new HashSet<Guid>(query.Ids));
                assets = Sort(assets, query.Ids);
            }
            else
            {
                var parsedQuery = ParseQuery(context, query.ODataQuery);

                assets = await assetRepository.QueryAsync(context.App.Id, parsedQuery);
            }

            await DenormalizeTagsAsync(context.App.Id, assets);

            return assets;
        }

        private static IResultList<IAssetEntity> Sort(IResultList<IAssetEntity> assets, IReadOnlyList<Guid> ids)
        {
            var sorted = ids.Select(id => assets.FirstOrDefault(x => x.Id == id)).Where(x => x != null);

            return ResultList.Create(assets.Total, sorted);
        }

        private Query ParseQuery(QueryContext context, string query)
        {
            try
            {
                var result = EdmAssetModel.Edm.ParseQuery(query).ToQuery();

                if (result.Filter != null)
                {
                    result.Filter = FilterTagTransformer.Transform(result.Filter, context.App.Id, tagService);
                }

                if (result.Sort.Count == 0)
                {
                    result.Sort.Add(new SortNode(new List<string> { "lastModified" }, SortOrder.Descending));
                }

                if (result.Take > options.MaxResults)
                {
                    result.Take = options.MaxResults;
                }

                return result;
            }
            catch (NotSupportedException)
            {
                throw new ValidationException("OData operation is not supported.");
            }
            catch (ODataException ex)
            {
                throw new ValidationException($"Failed to parse query: {ex.Message}", ex);
            }
        }

        private async Task DenormalizeTagsAsync(Guid appId, IEnumerable<IAssetEntity> assets)
        {
            var tags = new HashSet<string>(assets.Where(x => x.Tags != null).SelectMany(x => x.Tags).Distinct());

            var tagsById = await tagService.DenormalizeTagsAsync(appId, TagGroups.Assets, tags);

            foreach (var asset in assets)
            {
                if (asset.Tags?.Count > 0)
                {
                    var tagNames = asset.Tags.ToList();

                    asset.Tags.Clear();

                    foreach (var id in tagNames)
                    {
                        if (tagsById.TryGetValue(id, out var name))
                        {
                            asset.Tags.Add(name);
                        }
                    }
                }
                else
                {
                    asset.Tags?.Clear();
                }
            }
        }
    }
}
