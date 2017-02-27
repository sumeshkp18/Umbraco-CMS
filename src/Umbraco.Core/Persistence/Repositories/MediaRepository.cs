﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Umbraco.Core.Configuration.UmbracoSettings;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Models.EntityBase;
using Umbraco.Core.Models.Rdbms;
using Umbraco.Core.Cache;
using Umbraco.Core.Persistence.DatabaseModelDefinitions;
using Umbraco.Core.Persistence.Factories;
using Umbraco.Core.Persistence.Querying;
using Umbraco.Core.Persistence.SqlSyntax;
using Umbraco.Core.Persistence.UnitOfWork;

namespace Umbraco.Core.Persistence.Repositories
{
    /// <summary>
    /// Represents a repository for doing CRUD operations for <see cref="IMedia"/>
    /// </summary>
    internal class MediaRepository : RecycleBinRepository<int, IMedia>, IMediaRepository
    {
        private readonly IMediaTypeRepository _mediaTypeRepository;
        private readonly ITagRepository _tagRepository;
        private readonly ContentXmlRepository<IMedia> _contentXmlRepository;
        private readonly ContentPreviewRepository<IMedia> _contentPreviewRepository;

        public MediaRepository(IDatabaseUnitOfWork work, CacheHelper cache, ILogger logger, ISqlSyntaxProvider sqlSyntax, IMediaTypeRepository mediaTypeRepository, ITagRepository tagRepository, IContentSection contentSection)
            : base(work, cache, logger, sqlSyntax, contentSection)
        {
            if (mediaTypeRepository == null) throw new ArgumentNullException("mediaTypeRepository");
            if (tagRepository == null) throw new ArgumentNullException("tagRepository");
            _mediaTypeRepository = mediaTypeRepository;
            _tagRepository = tagRepository;
            _contentXmlRepository = new ContentXmlRepository<IMedia>(work, CacheHelper.CreateDisabledCacheHelper(), logger, sqlSyntax);
            _contentPreviewRepository = new ContentPreviewRepository<IMedia>(work, CacheHelper.CreateDisabledCacheHelper(), logger, sqlSyntax);
            EnsureUniqueNaming = contentSection.EnsureUniqueNaming;
        }

        public bool EnsureUniqueNaming { get; private set; }

        #region Overrides of RepositoryBase<int,IMedia>

        protected override IMedia PerformGet(int id)
        {
            var sql = GetBaseQuery(false);
            sql.Where(GetBaseWhereClause(), new { Id = id });
            sql.OrderByDescending<ContentVersionDto>(x => x.VersionDate, SqlSyntax);

            var dto = Database.Fetch<ContentVersionDto, ContentDto, NodeDto>(SqlSyntax.SelectTop(sql, 1)).FirstOrDefault();

            if (dto == null)
                return null;

            var content = CreateMediaFromDto(dto, dto.VersionId, sql);

            return content;
        }

        protected override IEnumerable<IMedia> PerformGetAll(params int[] ids)
        {
            var sql = GetBaseQuery(false);
            if (ids.Any())
            {
                sql.Where("umbracoNode.id in (@ids)", new { ids = ids });
            }

            return ProcessQuery(sql, new PagingSqlQuery(sql));
        }

        protected override IEnumerable<IMedia> PerformGetByQuery(IQuery<IMedia> query)
        {
            var sqlClause = GetBaseQuery(false);
            var translator = new SqlTranslator<IMedia>(sqlClause, query);
            var sql = translator.Translate()
                                .OrderBy<NodeDto>(x => x.SortOrder, SqlSyntax);

            return ProcessQuery(sql, new PagingSqlQuery(sql));
        }

        #endregion

        #region Overrides of PetaPocoRepositoryBase<int,IMedia>
        
        protected override Sql GetBaseQuery(BaseQueryType queryType)
        {
            var sql = new Sql();
            sql.Select(queryType == BaseQueryType.Count ? "COUNT(*)" : (queryType == BaseQueryType.Ids ? "cmsContentVersion.contentId" : "*"))
                .From<ContentVersionDto>(SqlSyntax)
                .InnerJoin<ContentDto>(SqlSyntax)
                .On<ContentVersionDto, ContentDto>(SqlSyntax, left => left.NodeId, right => right.NodeId)
                .InnerJoin<NodeDto>(SqlSyntax)
                .On<ContentDto, NodeDto>(SqlSyntax, left => left.NodeId, right => right.NodeId, SqlSyntax)
                //TODO: IF we want to enable querying on content type information this will need to be joined
                //.InnerJoin<ContentTypeDto>(SqlSyntax)
                //.On<ContentDto, ContentTypeDto>(SqlSyntax, left => left.ContentTypeId, right => right.NodeId, SqlSyntax);
                .Where<NodeDto>(x => x.NodeObjectType == NodeObjectTypeId, SqlSyntax);
            return sql;
        }

        protected override Sql GetBaseQuery(bool isCount)
        {
            return GetBaseQuery(isCount ? BaseQueryType.Count : BaseQueryType.FullSingle);
        }

        protected override string GetBaseWhereClause()
        {
            return "umbracoNode.id = @Id";
        }

        protected override IEnumerable<string> GetDeleteClauses()
        {
            var list = new List<string>
                           {
                               "DELETE FROM cmsTask WHERE nodeId = @Id",
                               "DELETE FROM umbracoUser2NodeNotify WHERE nodeId = @Id",
                               "DELETE FROM umbracoUser2NodePermission WHERE nodeId = @Id",
                               "DELETE FROM umbracoRelation WHERE parentId = @Id",
                               "DELETE FROM umbracoRelation WHERE childId = @Id",
                               "DELETE FROM cmsTagRelationship WHERE nodeId = @Id",
                               "DELETE FROM cmsDocument WHERE nodeId = @Id",
                               "DELETE FROM cmsPropertyData WHERE contentNodeId = @Id",
                               "DELETE FROM cmsPreviewXml WHERE nodeId = @Id",
                               "DELETE FROM cmsContentVersion WHERE ContentId = @Id",
                               "DELETE FROM cmsContentXml WHERE nodeId = @Id",
                               "DELETE FROM cmsContent WHERE nodeId = @Id",
                               "DELETE FROM umbracoNode WHERE id = @Id"
                           };
            return list;
        }

        protected override Guid NodeObjectTypeId
        {
            get { return new Guid(Constants.ObjectTypes.Media); }
        }

        #endregion

        #region Overrides of VersionableRepositoryBase<IContent>

        public override IEnumerable<IMedia> GetAllVersions(int id)
        {
            var sql = GetBaseQuery(false)
                .Where(GetBaseWhereClause(), new { Id = id })
                .OrderByDescending<ContentVersionDto>(x => x.VersionDate, SqlSyntax);
            return ProcessQuery(sql, new PagingSqlQuery(sql), true);
        }

        /// <summary>
        /// This is the underlying method that processes most queries for this repository
        /// </summary>
        /// <param name="sqlFull">
        /// The full SQL to select all media data 
        /// </param>
        /// <param name="pagingSqlQuery">
        /// The Id SQL to just return all media ids - used to process the properties for the media item
        /// </param>
        /// <param name="withCache"></param>
        /// <returns></returns>
        private IEnumerable<IMedia> ProcessQuery(Sql sqlFull, PagingSqlQuery pagingSqlQuery, bool withCache = false)
        {
            // fetch returns a list so it's ok to iterate it in this method
            var dtos = Database.Fetch<ContentVersionDto, ContentDto, NodeDto>(sqlFull);
            var content = new IMedia[dtos.Count];
            var defs = new List<DocumentDefinition>();

            //track the looked up content types, even though the content types are cached
            // they still need to be deep cloned out of the cache and we don't want to add
            // the overhead of deep cloning them on every item in this loop
            var contentTypes = new Dictionary<int, IMediaType>();

            for (var i = 0; i < dtos.Count; i++)
            {
                var dto = dtos[i];

                // if the cache contains the item, use it
                if (withCache)
                {
                    var cached = RuntimeCache.GetCacheItem<IMedia>(GetCacheIdKey<IMedia>(dto.NodeId));
                    if (cached != null)
                    {
                        content[i] = cached;
                        continue;
                    }
                }

                // else, need to fetch from the database
                // content type repository is full-cache so OK to get each one independently

                IMediaType contentType;
                if (contentTypes.ContainsKey(dto.ContentDto.ContentTypeId))
                {
                    contentType = contentTypes[dto.ContentDto.ContentTypeId];
                }
                else
                {
                    contentType = _mediaTypeRepository.Get(dto.ContentDto.ContentTypeId);
                    contentTypes[dto.ContentDto.ContentTypeId] = contentType;
                }
                
                content[i] = MediaFactory.BuildEntity(dto, contentType);

                // need properties
                defs.Add(new DocumentDefinition(
                    dto.NodeId,
                    dto.VersionId,
                    dto.VersionDate,
                    dto.ContentDto.NodeDto.CreateDate,
                    contentType
                ));
            }

            // load all properties for all documents from database in 1 query
            var propertyData = GetPropertyCollection(pagingSqlQuery, defs);

            // assign
            var dtoIndex = 0;
            foreach (var def in defs)
            {
                // move to corresponding item (which has to exist)
                while (dtos[dtoIndex].NodeId != def.Id) dtoIndex++;

                // complete the item
                var cc = content[dtoIndex];
                cc.Properties = propertyData[cc.Id];

                //on initial construction we don't want to have dirty properties tracked
                // http://issues.umbraco.org/issue/U4-1946
                cc.ResetDirtyProperties(false);
            }

            return content;
        }

        public override IMedia GetByVersion(Guid versionId)
        {
            var sql = GetBaseQuery(false);
            sql.Where("cmsContentVersion.VersionId = @VersionId", new { VersionId = versionId });
            sql.OrderByDescending<ContentVersionDto>(x => x.VersionDate, SqlSyntax);

            var dto = Database.Fetch<ContentVersionDto, ContentDto, NodeDto>(sql).FirstOrDefault();

            if (dto == null)
                return null;

            var content = CreateMediaFromDto(dto, versionId, sql);

            return content;
        }

        public void RebuildXmlStructures(Func<IMedia, XElement> serializer, int groupSize = 200, IEnumerable<int> contentTypeIds = null)
        {
            // the previous way of doing this was to run it all in one big transaction,
            // and to bulk-insert groups of xml rows - which works, until the transaction
            // times out - and besides, because v7 transactions are ReadCommited, it does
            // not bring much safety - so this reverts to updating each record individually,
            // and it may be slower in the end, but should be more resilient.

            var baseId = 0;
            var contentTypeIdsA = contentTypeIds == null ? new int[0] : contentTypeIds.ToArray();
            while (true)
            {
                // get the next group of nodes
                var query = GetBaseQuery(false);
                if (contentTypeIdsA.Length > 0)
                    query = query
                        .WhereIn<ContentDto>(x => x.ContentTypeId, contentTypeIdsA, SqlSyntax);
                query = query
                    .Where<NodeDto>(x => x.NodeId > baseId, SqlSyntax)
                    .OrderBy<NodeDto>(x => x.NodeId, SqlSyntax);
                var sql = SqlSyntax.SelectTop(query, groupSize);
                var xmlItems = ProcessQuery(sql, new PagingSqlQuery(sql))
                    .Select(x => new ContentXmlDto { NodeId = x.Id, Xml = serializer(x).ToString() })
                    .ToList();

                // no more nodes, break
                if (xmlItems.Count == 0) break;

                foreach (var xmlItem in xmlItems)
                {
                    try
                    {
                        // InsertOrUpdate tries to update first, which is good since it is what
                        // should happen in most cases, then it tries to insert, and it should work
                        // unless the node has been deleted, and we just report the exception
                        Database.InsertOrUpdate(xmlItem);
                    }
                    catch (Exception e)
                    {
                        Logger.Error<MediaRepository>("Could not rebuild XML for nodeId=" + xmlItem.NodeId, e);
                    }
                }
                baseId = xmlItems.Last().NodeId;
            }
        }

        public void AddOrUpdateContentXml(IMedia content, Func<IMedia, XElement> xml)
        {
            _contentXmlRepository.AddOrUpdate(new ContentXmlEntity<IMedia>(content, xml));
        }

        public void AddOrUpdatePreviewXml(IMedia content, Func<IMedia, XElement> xml)
        {
            _contentPreviewRepository.AddOrUpdate(new ContentPreviewEntity<IMedia>(content, xml));
        }

        protected override void PerformDeleteVersion(int id, Guid versionId)
        {
            Database.Delete<PreviewXmlDto>("WHERE nodeId = @Id AND versionId = @VersionId", new { Id = id, VersionId = versionId });
            Database.Delete<PropertyDataDto>("WHERE contentNodeId = @Id AND versionId = @VersionId", new { Id = id, VersionId = versionId });
            Database.Delete<ContentVersionDto>("WHERE ContentId = @Id AND VersionId = @VersionId", new { Id = id, VersionId = versionId });
        }

        #endregion

        #region Unit of Work Implementation

        protected override void PersistNewItem(IMedia entity)
        {
            ((Models.Media)entity).AddingEntity();

            //Ensure unique name on the same level
            entity.Name = EnsureUniqueNodeName(entity.ParentId, entity.Name);

            //Ensure that strings don't contain characters that are invalid in XML
            entity.SanitizeEntityPropertiesForXmlStorage();

            var factory = new MediaFactory(NodeObjectTypeId, entity.Id);
            var dto = factory.BuildDto(entity);

            //NOTE Should the logic below have some kind of fallback for empty parent ids ?
            //Logic for setting Path, Level and SortOrder
            var parent = Database.First<NodeDto>("WHERE id = @ParentId", new { ParentId = entity.ParentId });
            var level = parent.Level + 1;
            var maxSortOrder = Database.ExecuteScalar<int>(
                "SELECT coalesce(max(sortOrder),-1) FROM umbracoNode WHERE parentid = @ParentId AND nodeObjectType = @NodeObjectType",
                new { /*ParentId =*/ entity.ParentId, NodeObjectType = NodeObjectTypeId });
            var sortOrder = maxSortOrder + 1;

            //Create the (base) node data - umbracoNode
            var nodeDto = dto.ContentDto.NodeDto;
            nodeDto.Path = parent.Path;
            nodeDto.Level = short.Parse(level.ToString(CultureInfo.InvariantCulture));
            nodeDto.SortOrder = sortOrder;
            var o = Database.IsNew(nodeDto) ? Convert.ToInt32(Database.Insert(nodeDto)) : Database.Update(nodeDto);

            //Update with new correct path
            nodeDto.Path = string.Concat(parent.Path, ",", nodeDto.NodeId);
            nodeDto.ValidatePathWithException();
            Database.Update(nodeDto);

            //Update entity with correct values
            entity.Id = nodeDto.NodeId; //Set Id on entity to ensure an Id is set
            entity.Path = nodeDto.Path;
            entity.SortOrder = sortOrder;
            entity.Level = level;

            //Create the Content specific data - cmsContent
            var contentDto = dto.ContentDto;
            contentDto.NodeId = nodeDto.NodeId;
            Database.Insert(contentDto);

            //Create the first version - cmsContentVersion
            //Assumes a new Version guid and Version date (modified date) has been set
            dto.NodeId = nodeDto.NodeId;
            Database.Insert(dto);

            //Create the PropertyData for this version - cmsPropertyData
            var propertyFactory = new PropertyFactory(entity.ContentType.CompositionPropertyTypes.ToArray(), entity.Version, entity.Id);
            var propertyDataDtos = propertyFactory.BuildDto(entity.Properties);
            var keyDictionary = new Dictionary<int, int>();

            //Add Properties
            foreach (var propertyDataDto in propertyDataDtos)
            {
                var primaryKey = Convert.ToInt32(Database.Insert(propertyDataDto));
                keyDictionary.Add(propertyDataDto.PropertyTypeId, primaryKey);
            }

            //Update Properties with its newly set Id
            foreach (var property in entity.Properties)
            {
                property.Id = keyDictionary[property.PropertyTypeId];
            }

            UpdatePropertyTags(entity, _tagRepository);

            entity.ResetDirtyProperties();
        }

        protected override void PersistUpdatedItem(IMedia entity)
        {
            //Updates Modified date
            ((Models.Media)entity).UpdatingEntity();

            //Ensure unique name on the same level
            entity.Name = EnsureUniqueNodeName(entity.ParentId, entity.Name, entity.Id);

            //Ensure that strings don't contain characters that are invalid in XML
            entity.SanitizeEntityPropertiesForXmlStorage();

            //Look up parent to get and set the correct Path and update SortOrder if ParentId has changed
            if (entity.IsPropertyDirty("ParentId"))
            {
                var parent = Database.First<NodeDto>("WHERE id = @ParentId", new { ParentId = entity.ParentId });
                entity.Path = string.Concat(parent.Path, ",", entity.Id);
                entity.Level = parent.Level + 1;
                var maxSortOrder =
                    Database.ExecuteScalar<int>(
                        "SELECT coalesce(max(sortOrder),0) FROM umbracoNode WHERE parentid = @ParentId AND nodeObjectType = @NodeObjectType",
                        new { ParentId = entity.ParentId, NodeObjectType = NodeObjectTypeId });
                entity.SortOrder = maxSortOrder + 1;
            }

            var factory = new MediaFactory(NodeObjectTypeId, entity.Id);
            //Look up Content entry to get Primary for updating the DTO
            var contentDto = Database.SingleOrDefault<ContentDto>("WHERE nodeId = @Id", new { Id = entity.Id });
            factory.SetPrimaryKey(contentDto.PrimaryKey);
            var dto = factory.BuildDto(entity);

            //Updates the (base) node data - umbracoNode
            var nodeDto = dto.ContentDto.NodeDto;
            nodeDto.ValidatePathWithException();
            var o = Database.Update(nodeDto);

            //Only update this DTO if the contentType has actually changed
            if (contentDto.ContentTypeId != entity.ContentTypeId)
            {
                //Create the Content specific data - cmsContent
                var newContentDto = dto.ContentDto;
                Database.Update(newContentDto);
            }

            //In order to update the ContentVersion we need to retrieve its primary key id
            var contentVerDto = Database.SingleOrDefault<ContentVersionDto>("WHERE VersionId = @Version", new { Version = entity.Version });
            dto.Id = contentVerDto.Id;
            //Updates the current version - cmsContentVersion
            //Assumes a Version guid exists and Version date (modified date) has been set/updated
            Database.Update(dto);

            //Create the PropertyData for this version - cmsPropertyData
            var propertyFactory = new PropertyFactory(entity.ContentType.CompositionPropertyTypes.ToArray(), entity.Version, entity.Id);
            var propertyDataDtos = propertyFactory.BuildDto(entity.Properties);
            var keyDictionary = new Dictionary<int, int>();

            //Add Properties
            foreach (var propertyDataDto in propertyDataDtos)
            {
                if (propertyDataDto.Id > 0)
                {
                    Database.Update(propertyDataDto);
                }
                else
                {
                    int primaryKey = Convert.ToInt32(Database.Insert(propertyDataDto));
                    keyDictionary.Add(propertyDataDto.PropertyTypeId, primaryKey);
                }
            }

            //Update Properties with its newly set Id
            if (keyDictionary.Any())
            {
                foreach (var property in entity.Properties)
                {
                    if (keyDictionary.ContainsKey(property.PropertyTypeId) == false) continue;

                    property.Id = keyDictionary[property.PropertyTypeId];
                }
            }

            UpdatePropertyTags(entity, _tagRepository);

            entity.ResetDirtyProperties();
        }

        #endregion

        #region IRecycleBinRepository members

        protected override int RecycleBinId
        {
            get { return Constants.System.RecycleBinMedia; }
        }

        #endregion

        /// <summary>
        /// Gets paged media results
        /// </summary>
        /// <param name="query">Query to excute</param>
        /// <param name="pageIndex">Page number</param>
        /// <param name="pageSize">Page size</param>
        /// <param name="totalRecords">Total records query would return without paging</param>
        /// <param name="orderBy">Field to order by</param>
        /// <param name="orderDirection">Direction to order by</param>
        /// <param name="orderBySystemField">Flag to indicate when ordering by system field</param>
        /// <param name="filter">Search text filter</param>
        /// <returns>An Enumerable list of <see cref="IMedia"/> objects</returns>
        public IEnumerable<IMedia> GetPagedResultsByQuery(IQuery<IMedia> query, long pageIndex, int pageSize, out long totalRecords,
            string orderBy, Direction orderDirection, bool orderBySystemField, string filter = "")
        {
            var args = new List<object>();
            var sbWhere = new StringBuilder();
            Func<Tuple<string, object[]>> filterCallback = null;
            if (filter.IsNullOrWhiteSpace() == false)
            {
                sbWhere
                    .Append("AND (")
                    .Append(SqlSyntax.GetQuotedTableName("umbracoNode"))
                    .Append(".")
                    .Append(SqlSyntax.GetQuotedColumnName("text"))
                    .Append(" LIKE @")
                    .Append(args.Count)
                    .Append(")");
                args.Add("%" + filter + "%");

                filterCallback = () => new Tuple<string, object[]>(sbWhere.ToString().Trim(), args.ToArray());
            }

            return GetPagedResultsByQuery<ContentVersionDto>(query, pageIndex, pageSize, out totalRecords,
                new Tuple<string, string>("cmsContentVersion", "contentId"),
                (sqlFull, pagingSqlQuery) => ProcessQuery(sqlFull, pagingSqlQuery), orderBy, orderDirection, orderBySystemField,
                filterCallback);

        }

        /// <summary>
        /// Private method to create a media object from a ContentDto
        /// </summary>
        /// <param name="dto"></param>
        /// <param name="versionId"></param>
        /// <param name="docSql"></param>
        /// <returns></returns>
        private IMedia CreateMediaFromDto(ContentVersionDto dto, Guid versionId, Sql docSql)
        {
            var contentType = _mediaTypeRepository.Get(dto.ContentDto.ContentTypeId);
            
            var media = MediaFactory.BuildEntity(dto, contentType);

            var docDef = new DocumentDefinition(dto.NodeId, versionId, media.UpdateDate, media.CreateDate, contentType);

            var properties = GetPropertyCollection(new PagingSqlQuery(docSql), new[] { docDef });

            media.Properties = properties[dto.NodeId];

            //on initial construction we don't want to have dirty properties tracked
            // http://issues.umbraco.org/issue/U4-1946
            ((Entity)media).ResetDirtyProperties(false);
            return media;
        }

        private string EnsureUniqueNodeName(int parentId, string nodeName, int id = 0)
        {
            if (EnsureUniqueNaming == false)
                return nodeName;

            var sql = new Sql();
            sql.Select("*")
               .From<NodeDto>()
               .Where<NodeDto>(x => x.NodeObjectType == NodeObjectTypeId && x.ParentId == parentId && x.Text.StartsWith(nodeName));

            int uniqueNumber = 1;
            var currentName = nodeName;

            var dtos = Database.Fetch<NodeDto>(sql);
            if (dtos.Any())
            {
                var results = dtos.OrderBy(x => x.Text, new SimilarNodeNameComparer());
                foreach (var dto in results)
                {
                    if (id != 0 && id == dto.NodeId) continue;

                    if (dto.Text.ToLowerInvariant().Equals(currentName.ToLowerInvariant()))
                    {
                        currentName = nodeName + string.Format(" ({0})", uniqueNumber);
                        uniqueNumber++;
                    }
                }
            }

            return currentName;
        }

        /// <summary>
        /// Dispose disposable properties
        /// </summary>
        /// <remarks>
        /// Ensure the unit of work is disposed
        /// </remarks>
        protected override void DisposeResources()
        {
            _mediaTypeRepository.Dispose();
            _tagRepository.Dispose();
            _contentXmlRepository.Dispose();
            _contentPreviewRepository.Dispose();
        }
    }
}
