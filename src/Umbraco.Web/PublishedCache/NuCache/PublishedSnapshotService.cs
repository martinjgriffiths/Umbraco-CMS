﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using CSharpTest.Net.Collections;
using Newtonsoft.Json;
using Umbraco.Core;
using Umbraco.Core.Cache;
using Umbraco.Core.Configuration;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Models.Membership;
using Umbraco.Core.Models.PublishedContent;
using Umbraco.Core.Persistence;
using Umbraco.Core.Persistence.Dtos;
using Umbraco.Core.Persistence.Repositories;
using Umbraco.Core.Persistence.Repositories.Implement;
using Umbraco.Core.Scoping;
using Umbraco.Core.Services;
using Umbraco.Core.Services.Changes;
using Umbraco.Core.Services.Implement;
using Umbraco.Core.Strings;
using Umbraco.Web.Cache;
using Umbraco.Web.Install;
using Umbraco.Web.PublishedCache.NuCache.DataSource;
using Umbraco.Web.Routing;
using File = System.IO.File;

namespace Umbraco.Web.PublishedCache.NuCache
{

    internal class PublishedSnapshotService : PublishedSnapshotServiceBase
    {
        private readonly ServiceContext _serviceContext;
        private readonly IPublishedContentTypeFactory _publishedContentTypeFactory;
        private readonly IScopeProvider _scopeProvider;
        private readonly IDataSource _dataSource;
        private readonly IProfilingLogger _logger;
        private readonly IDocumentRepository _documentRepository;
        private readonly IMediaRepository _mediaRepository;
        private readonly IMemberRepository _memberRepository;
        private readonly IGlobalSettings _globalSettings;
        private readonly IEntityXmlSerializer _entitySerializer;
        private readonly IPublishedModelFactory _publishedModelFactory;
        private readonly IDefaultCultureAccessor _defaultCultureAccessor;
        private readonly UrlSegmentProviderCollection _urlSegmentProviders;

        // volatile because we read it with no lock
        private volatile bool _isReady;

        private readonly ContentStore _contentStore;
        private readonly ContentStore _mediaStore;
        private readonly SnapDictionary<int, Domain> _domainStore;
        private readonly object _storesLock = new object();

        private BPlusTree<int, ContentNodeKit> _localContentDb;
        private BPlusTree<int, ContentNodeKit> _localMediaDb;
        private bool _localDbExists;

        // define constant - determines whether to use cache when previewing
        // to store eg routes, property converted values, anything - caching
        // means faster execution, but uses memory - not sure if we want it
        // so making it configurable.
        public static readonly bool FullCacheWhenPreviewing = true;

        #region Constructors

        //private static int _singletonCheck;

        public PublishedSnapshotService(PublishedSnapshotServiceOptions options, IMainDom mainDom, IRuntimeState runtime,
            ServiceContext serviceContext, IPublishedContentTypeFactory publishedContentTypeFactory, IdkMap idkMap,
            IPublishedSnapshotAccessor publishedSnapshotAccessor, IVariationContextAccessor variationContextAccessor, IProfilingLogger logger, IScopeProvider scopeProvider,
            IDocumentRepository documentRepository, IMediaRepository mediaRepository, IMemberRepository memberRepository,
            IDefaultCultureAccessor defaultCultureAccessor,
            IDataSource dataSource, IGlobalSettings globalSettings,
            IEntityXmlSerializer entitySerializer,
            IPublishedModelFactory publishedModelFactory,
            UrlSegmentProviderCollection urlSegmentProviders)
            : base(publishedSnapshotAccessor, variationContextAccessor)
        {
            //if (Interlocked.Increment(ref _singletonCheck) > 1)
            //    throw new Exception("Singleton must be instantiated only once!");

            _serviceContext = serviceContext;
            _publishedContentTypeFactory = publishedContentTypeFactory;
            _dataSource = dataSource;
            _logger = logger;
            _scopeProvider = scopeProvider;
            _documentRepository = documentRepository;
            _mediaRepository = mediaRepository;
            _memberRepository = memberRepository;
            _defaultCultureAccessor = defaultCultureAccessor;
            _globalSettings = globalSettings;
            _urlSegmentProviders = urlSegmentProviders;

            // we need an Xml serializer here so that the member cache can support XPath,
            // for members this is done by navigating the serialized-to-xml member
            _entitySerializer = entitySerializer;
            _publishedModelFactory = publishedModelFactory;

            // we always want to handle repository events, configured or not
            // assuming no repository event will trigger before the whole db is ready
            // (ideally we'd have Upgrading.App vs Upgrading.Data application states...)
            InitializeRepositoryEvents();

            // however, the cache is NOT available until we are configured, because loading
            // content (and content types) from database cannot be consistent (see notes in "Handle
            // Notifications" region), so
            // - notifications will be ignored
            // - trying to obtain a published snapshot from the service will throw
            if (runtime.Level != RuntimeLevel.Run)
                return;

            if (options.IgnoreLocalDb == false)
            {
                var registered = mainDom.Register(
                    () =>
                    {
                        //"install" phase of MainDom
                        //this is inside of a lock in MainDom so this is guaranteed to run if MainDom was acquired and guaranteed
                        //to not run if MainDom wasn't acquired.
                        //If MainDom was not acquired, then _localContentDb and _localMediaDb will remain null which means this appdomain
                        //will load in published content via the DB and in that case this appdomain will probably not exist long enough to
                        //serve more than a page of content.

                        var path = GetLocalFilesPath();
                        var localContentDbPath = Path.Combine(path, "NuCache.Content.db");
                        var localMediaDbPath = Path.Combine(path, "NuCache.Media.db");
                        _localDbExists = File.Exists(localContentDbPath) && File.Exists(localMediaDbPath);
                        // if both local databases exist then GetTree will open them, else new databases will be created
                        _localContentDb = BTree.GetTree(localContentDbPath, _localDbExists);
                        _localMediaDb = BTree.GetTree(localMediaDbPath, _localDbExists);
                    },
                    () =>
                    {
                        //"release" phase of MainDom

                        lock (_storesLock)
                        {
                            _contentStore?.ReleaseLocalDb(); //null check because we could shut down before being assigned
                            _localContentDb = null;
                            _mediaStore?.ReleaseLocalDb(); //null check because we could shut down before being assigned
                            _localMediaDb = null;
                        }
                    });

                // stores are created with a db so they can write to it, but they do not read from it,
                // stores need to be populated, happens in OnResolutionFrozen which uses _localDbExists to
                // figure out whether it can read the databases or it should populate them from sql
                _contentStore = new ContentStore(publishedSnapshotAccessor, variationContextAccessor, logger, _localContentDb);
                _mediaStore = new ContentStore(publishedSnapshotAccessor, variationContextAccessor, logger, _localMediaDb);
            }
            else
            {
                _contentStore = new ContentStore(publishedSnapshotAccessor, variationContextAccessor, logger);
                _mediaStore = new ContentStore(publishedSnapshotAccessor, variationContextAccessor, logger);
            }

            _domainStore = new SnapDictionary<int, Domain>();

            LoadCachesOnStartup();

            Guid GetUid(ContentStore store, int id) => store.LiveSnapshot.Get(id)?.Uid ?? default;
            int GetId(ContentStore store, Guid uid) => store.LiveSnapshot.Get(uid)?.Id ?? default;

            if (idkMap != null)
            {
                idkMap.SetMapper(UmbracoObjectTypes.Document, id => GetUid(_contentStore, id), uid => GetId(_contentStore, uid));
                idkMap.SetMapper(UmbracoObjectTypes.Media, id => GetUid(_mediaStore, id), uid => GetId(_mediaStore, uid));
            }
        }

        private void LoadCachesOnStartup()
        {
            lock (_storesLock)
            {
                // populate the stores


                var okContent = false;
                var okMedia = false;

                try
                {
                    if (_localDbExists)
                    {
                        okContent = LockAndLoadContent(scope => LoadContentFromLocalDbLocked(true));
                        if (!okContent)
                            _logger.Warn<PublishedSnapshotService>("Loading content from local db raised warnings, will reload from database.");
                        okMedia = LockAndLoadMedia(scope => LoadMediaFromLocalDbLocked(true));
                        if (!okMedia)
                            _logger.Warn<PublishedSnapshotService>("Loading media from local db raised warnings, will reload from database.");
                    }

                    if (!okContent)
                        LockAndLoadContent(scope => LoadContentFromDatabaseLocked(scope, true));

                    if (!okMedia)
                        LockAndLoadMedia(scope => LoadMediaFromDatabaseLocked(scope, true));

                    LockAndLoadDomains();
                }
                catch (Exception ex)
                {
                    _logger.Fatal<PublishedSnapshotService>(ex, "Panic, exception while loading cache data.");
                    throw;
                }

                // finally, cache is ready!
                _isReady = true;
            }
        }

        private void InitializeRepositoryEvents()
        {
            // TODO: The reason these events are in the repository is for legacy, the events should exist at the service
            // level now since we can fire these events within the transaction... so move the events to service level

            // plug repository event handlers
            // these trigger within the transaction to ensure consistency
            // and are used to maintain the central, database-level XML cache
            DocumentRepository.ScopeEntityRemove += OnContentRemovingEntity;
            //ContentRepository.RemovedVersion += OnContentRemovedVersion;
            DocumentRepository.ScopedEntityRefresh += OnContentRefreshedEntity;
            MediaRepository.ScopeEntityRemove += OnMediaRemovingEntity;
            //MediaRepository.RemovedVersion += OnMediaRemovedVersion;
            MediaRepository.ScopedEntityRefresh += OnMediaRefreshedEntity;
            MemberRepository.ScopeEntityRemove += OnMemberRemovingEntity;
            //MemberRepository.RemovedVersion += OnMemberRemovedVersion;
            MemberRepository.ScopedEntityRefresh += OnMemberRefreshedEntity;

            // plug
            ContentTypeService.ScopedRefreshedEntity += OnContentTypeRefreshedEntity;
            MediaTypeService.ScopedRefreshedEntity += OnMediaTypeRefreshedEntity;
            MemberTypeService.ScopedRefreshedEntity += OnMemberTypeRefreshedEntity;
        }

        private void TearDownRepositoryEvents()
        {
            DocumentRepository.ScopeEntityRemove -= OnContentRemovingEntity;
            //ContentRepository.RemovedVersion -= OnContentRemovedVersion;
            DocumentRepository.ScopedEntityRefresh -= OnContentRefreshedEntity;
            MediaRepository.ScopeEntityRemove -= OnMediaRemovingEntity;
            //MediaRepository.RemovedVersion -= OnMediaRemovedVersion;
            MediaRepository.ScopedEntityRefresh -= OnMediaRefreshedEntity;
            MemberRepository.ScopeEntityRemove -= OnMemberRemovingEntity;
            //MemberRepository.RemovedVersion -= OnMemberRemovedVersion;
            MemberRepository.ScopedEntityRefresh -= OnMemberRefreshedEntity;

            ContentTypeService.ScopedRefreshedEntity -= OnContentTypeRefreshedEntity;
            MediaTypeService.ScopedRefreshedEntity -= OnMediaTypeRefreshedEntity;
            MemberTypeService.ScopedRefreshedEntity -= OnMemberTypeRefreshedEntity;
        }

        public override void Dispose()
        {
            TearDownRepositoryEvents();
            base.Dispose();
        }

        #endregion

        #region Local files

        private string GetLocalFilesPath()
        {
            var path = Path.Combine(_globalSettings.LocalTempPath, "NuCache");

            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            return path;
        }

        private void DeleteLocalFilesForContent()
        {
            if (_isReady && _localContentDb != null)
                throw new InvalidOperationException("Cannot delete local files while the cache uses them.");

            var path = GetLocalFilesPath();
            var localContentDbPath = Path.Combine(path, "NuCache.Content.db");
            if (File.Exists(localContentDbPath))
                File.Delete(localContentDbPath);
        }

        private void DeleteLocalFilesForMedia()
        {
            if (_isReady && _localMediaDb != null)
                throw new InvalidOperationException("Cannot delete local files while the cache uses them.");

            var path = GetLocalFilesPath();
            var localMediaDbPath = Path.Combine(path, "NuCache.Media.db");
            if (File.Exists(localMediaDbPath))
                File.Delete(localMediaDbPath);
        }

        #endregion

        #region Environment

        public override bool EnsureEnvironment(out IEnumerable<string> errors)
        {
            // must have app_data and be able to write files into it
            var ok = FilePermissionHelper.TryCreateDirectory(GetLocalFilesPath());
            errors = ok ? Enumerable.Empty<string>() : new[] { "NuCache local files." };
            return ok;
        }

        #endregion

        #region Populate Stores

        // sudden panic... but in RepeatableRead can a content that I haven't already read, be removed
        // before I read it? NO! because the WHOLE content tree is read-locked using WithReadLocked.
        // don't panic.

        private bool LockAndLoadContent(Func<IScope, bool> action)
        {


            // first get a writer, then a scope
            // if there already is a scope, the writer will attach to it
            // otherwise, it will only exist here - cheap
            using (_contentStore.GetScopedWriteLock(_scopeProvider))
            using (var scope = _scopeProvider.CreateScope())
            {
                scope.ReadLock(Constants.Locks.ContentTree);
                var ok = action(scope);
                scope.Complete();
                return ok;
            }
        }

        private bool LoadContentFromDatabaseLocked(IScope scope, bool onStartup)
        {
            // locks:
            // contentStore is wlocked (1 thread)
            // content (and types) are read-locked

            var contentTypes = _serviceContext.ContentTypeService.GetAll()
                .Select(x => _publishedContentTypeFactory.CreateContentType(x));

            _contentStore.SetAllContentTypes(contentTypes);

            using (_logger.TraceDuration<PublishedSnapshotService>("Loading content from database"))
            {
                // beware! at that point the cache is inconsistent,
                // assuming we are going to SetAll content items!

                _localContentDb?.Clear();

                // IMPORTANT GetAllContentSources sorts kits by level + parentId + sortOrder
                var kits = _dataSource.GetAllContentSources(scope);
                return onStartup ? _contentStore.SetAllFastSorted(kits) : _contentStore.SetAll(kits);
            }
        }

        private bool LoadContentFromLocalDbLocked(bool onStartup)
        {
            var contentTypes = _serviceContext.ContentTypeService.GetAll()
                    .Select(x => _publishedContentTypeFactory.CreateContentType(x));
            _contentStore.SetAllContentTypes(contentTypes);

            using (_logger.TraceDuration<PublishedSnapshotService>("Loading content from local cache file"))
            {
                // beware! at that point the cache is inconsistent,
                // assuming we are going to SetAll content items!

                var kits = _localContentDb.Select(x => x.Value)
                    .OrderBy(x => x.Node.Level)
                    .ThenBy(x => x.Node.ParentContentId)
                    .ThenBy(x => x.Node.SortOrder); // IMPORTANT sort by level + parentId + sortOrder
                return onStartup ? _contentStore.SetAllFastSorted(kits) : _contentStore.SetAll(kits);
            }
        }

        // keep these around - might be useful

        //private void LoadContentBranch(IContent content)
        //{
        //    LoadContent(content);

        //    foreach (var child in content.Children())
        //        LoadContentBranch(child);
        //}

        //private void LoadContent(IContent content)
        //{
        //    var contentService = _serviceContext.ContentService as ContentService;
        //    var newest = content;
        //    var published = newest.Published
        //        ? newest
        //        : (newest.HasPublishedVersion ? contentService.GetByVersion(newest.PublishedVersionGuid) : null);

        //    var contentNode = CreateContentNode(newest, published);
        //    _contentStore.Set(contentNode);
        //}

        private bool LockAndLoadMedia(Func<IScope, bool> action)
        {
            // see note in LockAndLoadContent
            using (_mediaStore.GetScopedWriteLock(_scopeProvider))
            using (var scope = _scopeProvider.CreateScope())
            {
                scope.ReadLock(Constants.Locks.MediaTree);
                var ok = action(scope);
                scope.Complete();
                return ok;
            }
        }

        private bool LoadMediaFromDatabaseLocked(IScope scope, bool onStartup)
        {
            // locks & notes: see content

            var mediaTypes = _serviceContext.MediaTypeService.GetAll()
                .Select(x => _publishedContentTypeFactory.CreateContentType(x));
            _mediaStore.SetAllContentTypes(mediaTypes);

            using (_logger.TraceDuration<PublishedSnapshotService>("Loading media from database"))
            {
                // beware! at that point the cache is inconsistent,
                // assuming we are going to SetAll content items!

                _localMediaDb?.Clear();

                _logger.Debug<PublishedSnapshotService>("Loading media from database...");
                // IMPORTANT GetAllMediaSources sorts kits by level + parentId + sortOrder
                var kits = _dataSource.GetAllMediaSources(scope);
                return onStartup ? _mediaStore.SetAllFastSorted(kits) : _mediaStore.SetAll(kits);
            }
        }

        private bool LoadMediaFromLocalDbLocked(bool onStartup)
        {
            var mediaTypes = _serviceContext.MediaTypeService.GetAll()
                    .Select(x => _publishedContentTypeFactory.CreateContentType(x));
            _mediaStore.SetAllContentTypes(mediaTypes);

            using (_logger.TraceDuration<PublishedSnapshotService>("Loading media from local cache file"))
            {
                // beware! at that point the cache is inconsistent,
                // assuming we are going to SetAll content items!

                var kits = _localMediaDb.Select(x => x.Value)
                    .OrderBy(x => x.Node.Level)
                    .ThenBy(x => x.Node.ParentContentId)
                    .ThenBy(x => x.Node.SortOrder); // IMPORTANT sort by level + parentId + sortOrder
                return onStartup ? _mediaStore.SetAllFastSorted(kits) : _mediaStore.SetAll(kits);
            }

        }

        // keep these around - might be useful

        //private void LoadMediaBranch(IMedia media)
        //{
        //    LoadMedia(media);

        //    foreach (var child in media.Children())
        //        LoadMediaBranch(child);
        //}

        //private void LoadMedia(IMedia media)
        //{
        //    var mediaType = _contentTypeCache.Get(PublishedItemType.Media, media.ContentTypeId);

        //    var mediaData = new ContentData
        //    {
        //        Name = media.Name,
        //        Published = true,
        //        Version = media.Version,
        //        VersionDate = media.UpdateDate,
        //        WriterId = media.CreatorId, // what else?
        //        TemplateId = -1, // have none
        //        Properties = GetPropertyValues(media)
        //    };

        //    var mediaNode = new ContentNode(media.Id, mediaType,
        //        media.Level, media.Path, media.SortOrder,
        //        media.ParentId, media.CreateDate, media.CreatorId,
        //        null, mediaData);

        //    _mediaStore.Set(mediaNode);
        //}

        //private Dictionary<string, object> GetPropertyValues(IContentBase content)
        //{
        //    var propertyEditorResolver = PropertyEditorResolver.Current; // should inject

        //    return content
        //        .Properties
        //        .Select(property =>
        //        {
        //            var e = propertyEditorResolver.GetByAlias(property.PropertyType.PropertyEditorAlias);
        //            var v = e == null
        //                ? property.Value
        //                : e.ValueEditor.ConvertDbToString(property, property.PropertyType, _serviceContext.DataTypeService);
        //            return new KeyValuePair<string, object>(property.Alias, v);
        //        })
        //        .ToDictionary(x => x.Key, x => x.Value);
        //}

        //private ContentData CreateContentData(IContent content)
        //{
        //    return new ContentData
        //    {
        //        Name = content.Name,
        //        Published = content.Published,
        //        Version = content.Version,
        //        VersionDate = content.UpdateDate,
        //        WriterId = content.WriterId,
        //        TemplateId = content.Template == null ? -1 : content.Template.Id,
        //        Properties = GetPropertyValues(content)
        //    };
        //}

        //private ContentNode CreateContentNode(IContent newest, IContent published)
        //{
        //    var contentType = _contentTypeCache.Get(PublishedItemType.Content, newest.ContentTypeId);

        //    var draftData = newest.Published
        //        ? null
        //        : CreateContentData(newest);

        //    var publishedData = newest.Published
        //        ? CreateContentData(newest)
        //        : (published == null ? null : CreateContentData(published));

        //    var contentNode = new ContentNode(newest.Id, contentType,
        //        newest.Level, newest.Path, newest.SortOrder,
        //        newest.ParentId, newest.CreateDate, newest.CreatorId,
        //        draftData, publishedData);

        //    return contentNode;
        //}

        private void LockAndLoadDomains()
        {
            // see note in LockAndLoadContent
            using (_domainStore.GetScopedWriteLock(_scopeProvider))
            using (var scope = _scopeProvider.CreateScope())
            {
                scope.ReadLock(Constants.Locks.Domains);
                LoadDomainsLocked();
                scope.Complete();
            }
        }

        private void LoadDomainsLocked()
        {
            var domains = _serviceContext.DomainService.GetAll(true);
            foreach (var domain in domains
                .Where(x => x.RootContentId.HasValue && x.LanguageIsoCode.IsNullOrWhiteSpace() == false)
                .Select(x => new Domain(x.Id, x.DomainName, x.RootContentId.Value, CultureInfo.GetCultureInfo(x.LanguageIsoCode), x.IsWildcard)))
            {
                _domainStore.Set(domain.Id, domain);
            }
        }

        #endregion

        #region Handle Notifications

        // note: if the service is not ready, ie _isReady is false, then notifications are ignored

        // SetUmbracoVersionStep issues a DistributedCache.Instance.RefreshAll...() call which should cause
        // the entire content, media etc caches to reload from database -- and then the app restarts -- however,
        // at the time SetUmbracoVersionStep runs, Umbraco is not fully initialized and therefore some property
        // value converters, etc are not registered, and rebuilding the NuCache may not work properly.
        //
        // More details: ApplicationContext.IsConfigured being false, ApplicationEventHandler.ExecuteWhen... is
        // called and in most cases events are skipped, so property value converters are not registered or
        // removed, so PublishedPropertyType either initializes with the wrong converter, or throws because it
        // detects more than one converter for a property type.
        //
        // It's not an issue for XmlStore - the app restart takes place *after* the install has refreshed the
        // cache, and XmlStore just writes a new umbraco.config file upon RefreshAll, so that's OK.
        //
        // But for NuCache... we cannot rebuild the cache now. So it will NOT work and we are not fixing it,
        // because now we should ALWAYS run with the database server messenger, and then the RefreshAll will
        // be processed as soon as we are configured and the messenger processes instructions.

        // note: notifications for content type and data type changes should be invoked with the
        // pure live model factory, if any, locked and refreshed - see ContentTypeCacheRefresher and
        // DataTypeCacheRefresher

        public override void Notify(ContentCacheRefresher.JsonPayload[] payloads, out bool draftChanged, out bool publishedChanged)
        {
            // no cache, trash everything
            if (_isReady == false)
            {
                DeleteLocalFilesForContent();
                draftChanged = publishedChanged = true;
                return;
            }

            using (_contentStore.GetScopedWriteLock(_scopeProvider))
            {
                NotifyLocked(payloads, out bool draftChanged2, out bool publishedChanged2);
                draftChanged = draftChanged2;
                publishedChanged = publishedChanged2;
            }

            if (draftChanged || publishedChanged)
                ((PublishedSnapshot)CurrentPublishedSnapshot)?.Resync();
        }

        private void NotifyLocked(IEnumerable<ContentCacheRefresher.JsonPayload> payloads, out bool draftChanged, out bool publishedChanged)
        {
            publishedChanged = false;
            draftChanged = false;

            // locks:
            // content (and content types) are read-locked while reading content
            // contentStore is wlocked (so readable, only no new views)
            // and it can be wlocked by 1 thread only at a time
            // contentStore is write-locked during changes

            foreach (var payload in payloads)
            {
                _logger.Debug<PublishedSnapshotService>("Notified {ChangeTypes} for content {ContentId}", payload.ChangeTypes, payload.Id);

                if (payload.ChangeTypes.HasType(TreeChangeTypes.RefreshAll))
                {
                    using (var scope = _scopeProvider.CreateScope())
                    {
                        scope.ReadLock(Constants.Locks.ContentTree);
                        LoadContentFromDatabaseLocked(scope, false);
                        scope.Complete();
                    }
                    draftChanged = publishedChanged = true;
                    continue;
                }

                if (payload.ChangeTypes.HasType(TreeChangeTypes.Remove))
                {
                    if (_contentStore.Clear(payload.Id))
                        draftChanged = publishedChanged = true;
                    continue;
                }

                if (payload.ChangeTypes.HasTypesNone(TreeChangeTypes.RefreshNode | TreeChangeTypes.RefreshBranch))
                {
                    // ?!
                    continue;
                }

                // TODO: should we do some RV check here? (later)

                var capture = payload;
                using (var scope = _scopeProvider.CreateScope())
                {
                    scope.ReadLock(Constants.Locks.ContentTree);

                    if (capture.ChangeTypes.HasType(TreeChangeTypes.RefreshBranch))
                    {
                        // ?? should we do some RV check here?
                        // IMPORTANT GetbranchContentSources sorts kits by level and by sort order
                        var kits = _dataSource.GetBranchContentSources(scope, capture.Id);
                        _contentStore.SetBranch(capture.Id, kits);
                    }
                    else
                    {
                        // ?? should we do some RV check here?
                        var kit = _dataSource.GetContentSource(scope, capture.Id);
                        if (kit.IsEmpty)
                        {
                            _contentStore.Clear(capture.Id);
                        }
                        else
                        {
                            _contentStore.Set(kit);
                        }
                    }

                    scope.Complete();
                }

                // ?? cannot tell really because we're not doing RV checks
                draftChanged = publishedChanged = true;
            }
        }

        /// <inheritdoc />
        public override void Notify(MediaCacheRefresher.JsonPayload[] payloads, out bool anythingChanged)
        {
            // no cache, trash everything
            if (_isReady == false)
            {
                DeleteLocalFilesForMedia();
                anythingChanged = true;
                return;
            }

            using (_mediaStore.GetScopedWriteLock(_scopeProvider))
            {
                NotifyLocked(payloads, out bool anythingChanged2);
                anythingChanged = anythingChanged2;
            }

            if (anythingChanged)
                ((PublishedSnapshot)CurrentPublishedSnapshot)?.Resync();
        }

        private void NotifyLocked(IEnumerable<MediaCacheRefresher.JsonPayload> payloads, out bool anythingChanged)
        {
            anythingChanged = false;

            // locks:
            // see notes for content cache refresher

            foreach (var payload in payloads)
            {
                _logger.Debug<PublishedSnapshotService>("Notified {ChangeTypes} for media {MediaId}", payload.ChangeTypes, payload.Id);

                if (payload.ChangeTypes.HasType(TreeChangeTypes.RefreshAll))
                {
                    using (var scope = _scopeProvider.CreateScope())
                    {
                        scope.ReadLock(Constants.Locks.MediaTree);
                        LoadMediaFromDatabaseLocked(scope, false);
                        scope.Complete();
                    }
                    anythingChanged = true;
                    continue;
                }

                if (payload.ChangeTypes.HasType(TreeChangeTypes.Remove))
                {
                    if (_mediaStore.Clear(payload.Id))
                        anythingChanged = true;
                    continue;
                }

                if (payload.ChangeTypes.HasTypesNone(TreeChangeTypes.RefreshNode | TreeChangeTypes.RefreshBranch))
                {
                    // ?!
                    continue;
                }

                // TODO: should we do some RV checks here? (later)

                var capture = payload;
                using (var scope = _scopeProvider.CreateScope())
                {
                    scope.ReadLock(Constants.Locks.MediaTree);

                    if (capture.ChangeTypes.HasType(TreeChangeTypes.RefreshBranch))
                    {
                        // ?? should we do some RV check here?
                        // IMPORTANT GetbranchContentSources sorts kits by level and by sort order
                        var kits = _dataSource.GetBranchMediaSources(scope, capture.Id);
                        _mediaStore.SetBranch(capture.Id, kits);
                    }
                    else
                    {
                        // ?? should we do some RV check here?
                        var kit = _dataSource.GetMediaSource(scope, capture.Id);
                        if (kit.IsEmpty)
                        {
                            _mediaStore.Clear(capture.Id);
                        }
                        else
                        {
                            _mediaStore.Set(kit);
                        }
                    }

                    scope.Complete();
                }

                // ?? cannot tell really because we're not doing RV checks
                anythingChanged = true;
            }
        }

        /// <inheritdoc />
        public override void Notify(ContentTypeCacheRefresher.JsonPayload[] payloads)
        {
            // no cache, nothing we can do
            if (_isReady == false)
                return;

            foreach (var payload in payloads)
                _logger.Debug<PublishedSnapshotService>("Notified {ChangeTypes} for {ItemType} {ItemId}", payload.ChangeTypes, payload.ItemType, payload.Id);

            Notify<IContentType>(_contentStore, payloads, RefreshContentTypesLocked);
            Notify<IMediaType>(_mediaStore, payloads, RefreshMediaTypesLocked);

            if (_publishedModelFactory.IsLiveFactory())
            {
                //In the case of Pure Live - we actually need to refresh all of the content and the media
                //see https://github.com/umbraco/Umbraco-CMS/issues/5671
                //The underlying issue is that in Pure Live the ILivePublishedModelFactory will re-compile all of the classes/models
                //into a new DLL for the application which includes both content types and media types.
                //Since the models in the cache are based on these actual classes, all of the objects in the cache need to be updated
                //to use the newest version of the class.
                using (_contentStore.GetScopedWriteLock(_scopeProvider))
                using (_mediaStore.GetScopedWriteLock(_scopeProvider))
                {
                    NotifyLocked(new[] { new ContentCacheRefresher.JsonPayload(0, TreeChangeTypes.RefreshAll) }, out var draftChanged, out var publishedChanged);
                    NotifyLocked(new[] { new MediaCacheRefresher.JsonPayload(0, TreeChangeTypes.RefreshAll) }, out var anythingChanged);
                }
            }

            ((PublishedSnapshot)CurrentPublishedSnapshot)?.Resync();
        }

        private void Notify<T>(ContentStore store, ContentTypeCacheRefresher.JsonPayload[] payloads, Action<List<int>, List<int>, List<int>, List<int>> action)
            where T : IContentTypeComposition
        {
            if (payloads.Length == 0) return; //nothing to do

            var nameOfT = typeof(T).Name;

            List<int> removedIds = null, refreshedIds = null, otherIds = null, newIds = null;

            foreach (var payload in payloads)
            {
                if (payload.ItemType != nameOfT) continue;

                if (payload.ChangeTypes.HasType(ContentTypeChangeTypes.Remove))
                    AddToList(ref removedIds, payload.Id);
                else if (payload.ChangeTypes.HasType(ContentTypeChangeTypes.RefreshMain))
                    AddToList(ref refreshedIds, payload.Id);
                else if (payload.ChangeTypes.HasType(ContentTypeChangeTypes.RefreshOther))
                    AddToList(ref otherIds, payload.Id);
                else if (payload.ChangeTypes.HasType(ContentTypeChangeTypes.Create))
                    AddToList(ref newIds, payload.Id);
            }

            if (removedIds.IsCollectionEmpty() && refreshedIds.IsCollectionEmpty() && otherIds.IsCollectionEmpty() && newIds.IsCollectionEmpty()) return;

            using (store.GetScopedWriteLock(_scopeProvider))
            {
                // ReSharper disable AccessToModifiedClosure
                action(removedIds, refreshedIds, otherIds, newIds);
                // ReSharper restore AccessToModifiedClosure
            }
        }

        public override void Notify(DataTypeCacheRefresher.JsonPayload[] payloads)
        {
            // no cache, nothing we can do
            if (_isReady == false)
                return;

            var idsA = payloads.Select(x => x.Id).ToArray();

            foreach (var payload in payloads)
                _logger.Debug<PublishedSnapshotService>("Notified {RemovedStatus} for data type {DataTypeId}",
                    payload.Removed ? "Removed" : "Refreshed",
                    payload.Id);

            using (_contentStore.GetScopedWriteLock(_scopeProvider))
            using (_mediaStore.GetScopedWriteLock(_scopeProvider))
            {
                // TODO: need to add a datatype lock
                // this is triggering datatypes reload in the factory, and right after we create some
                // content types by loading them ... there's a race condition here, which would require
                // some locking on datatypes
                _publishedContentTypeFactory.NotifyDataTypeChanges(idsA);

                using (var scope = _scopeProvider.CreateScope())
                {
                    scope.ReadLock(Constants.Locks.ContentTree);
                    _contentStore.UpdateDataTypes(idsA, id => CreateContentType(PublishedItemType.Content, id));
                    scope.Complete();
                }

                using (var scope = _scopeProvider.CreateScope())
                {
                    scope.ReadLock(Constants.Locks.MediaTree);
                    _mediaStore.UpdateDataTypes(idsA, id => CreateContentType(PublishedItemType.Media, id));
                    scope.Complete();
                }
            }

            ((PublishedSnapshot)CurrentPublishedSnapshot)?.Resync();
        }

        public override void Notify(DomainCacheRefresher.JsonPayload[] payloads)
        {
            // no cache, nothing we can do
            if (_isReady == false)
                return;

            // see note in LockAndLoadContent
            using (_domainStore.GetScopedWriteLock(_scopeProvider))
            {
                foreach (var payload in payloads)
                {
                    switch (payload.ChangeType)
                    {
                        case DomainChangeTypes.RefreshAll:
                            using (var scope = _scopeProvider.CreateScope())
                            {
                                scope.ReadLock(Constants.Locks.Domains);
                                LoadDomainsLocked();
                                scope.Complete();
                            }
                            break;
                        case DomainChangeTypes.Remove:
                            _domainStore.Clear(payload.Id);
                            break;
                        case DomainChangeTypes.Refresh:
                            var domain = _serviceContext.DomainService.GetById(payload.Id);
                            if (domain == null) continue;
                            if (domain.RootContentId.HasValue == false) continue; // anomaly
                            if (domain.LanguageIsoCode.IsNullOrWhiteSpace()) continue; // anomaly
                            var culture = CultureInfo.GetCultureInfo(domain.LanguageIsoCode);
                            _domainStore.Set(domain.Id, new Domain(domain.Id, domain.DomainName, domain.RootContentId.Value, culture, domain.IsWildcard));
                            break;
                    }
                }
            }
        }

        //Methods used to prevent allocations of lists        
        private void AddToList(ref List<int> list, int val) => GetOrCreateList(ref list).Add(val);
        private List<int> GetOrCreateList(ref List<int> list) => list ?? (list = new List<int>());

        #endregion

        #region Content Types

        private IReadOnlyCollection<IPublishedContentType> CreateContentTypes(PublishedItemType itemType, int[] ids)
        {
            // XxxTypeService.GetAll(empty) returns everything!
            if (ids.Length == 0)
                return Array.Empty<IPublishedContentType>();

            IEnumerable<IContentTypeComposition> contentTypes;
            switch (itemType)
            {
                case PublishedItemType.Content:
                    contentTypes = _serviceContext.ContentTypeService.GetAll(ids);
                    break;
                case PublishedItemType.Media:
                    contentTypes = _serviceContext.MediaTypeService.GetAll(ids);
                    break;
                case PublishedItemType.Member:
                    contentTypes = _serviceContext.MemberTypeService.GetAll(ids);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(itemType));
            }

            // some may be missing - not checking here

            return contentTypes.Select(x => _publishedContentTypeFactory.CreateContentType(x)).ToList();
        }

        private IPublishedContentType CreateContentType(PublishedItemType itemType, int id)
        {
            IContentTypeComposition contentType;
            switch (itemType)
            {
                case PublishedItemType.Content:
                    contentType = _serviceContext.ContentTypeService.Get(id);
                    break;
                case PublishedItemType.Media:
                    contentType = _serviceContext.MediaTypeService.Get(id);
                    break;
                case PublishedItemType.Member:
                    contentType = _serviceContext.MemberTypeService.Get(id);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(itemType));
            }

            return contentType == null ? null : _publishedContentTypeFactory.CreateContentType(contentType);
        }

        private void RefreshContentTypesLocked(List<int> removedIds, List<int> refreshedIds, List<int> otherIds, List<int> newIds)
        {
            if (removedIds.IsCollectionEmpty() && refreshedIds.IsCollectionEmpty() && otherIds.IsCollectionEmpty() && newIds.IsCollectionEmpty())
                return;

            // locks:
            // content (and content types) are read-locked while reading content
            // contentStore is wlocked (so readable, only no new views)
            // and it can be wlocked by 1 thread only at a time

            using (var scope = _scopeProvider.CreateScope())
            {
                scope.ReadLock(Constants.Locks.ContentTypes);

                var typesA = refreshedIds.IsCollectionEmpty()
                    ? Array.Empty<IPublishedContentType>()
                    : CreateContentTypes(PublishedItemType.Content, refreshedIds.ToArray()).ToArray();

                var kits = refreshedIds.IsCollectionEmpty()
                    ? Array.Empty<ContentNodeKit>()
                    : _dataSource.GetTypeContentSources(scope, refreshedIds).ToArray();

                _contentStore.UpdateContentTypes(removedIds, typesA, kits);
                if (!otherIds.IsCollectionEmpty())
                    _contentStore.UpdateContentTypes(CreateContentTypes(PublishedItemType.Content, otherIds.ToArray()));
                if (!newIds.IsCollectionEmpty())
                    _contentStore.NewContentTypes(CreateContentTypes(PublishedItemType.Content, newIds.ToArray()));
                scope.Complete();
            }
        }

        private void RefreshMediaTypesLocked(List<int> removedIds, List<int> refreshedIds, List<int> otherIds, List<int> newIds)
        {
            if (removedIds.IsCollectionEmpty() && refreshedIds.IsCollectionEmpty() && otherIds.IsCollectionEmpty() && newIds.IsCollectionEmpty())
                return;

            // locks:
            // media (and content types) are read-locked while reading media
            // mediaStore is wlocked (so readable, only no new views)
            // and it can be wlocked by 1 thread only at a time

            using (var scope = _scopeProvider.CreateScope())
            {
                scope.ReadLock(Constants.Locks.MediaTypes);

                var typesA = refreshedIds == null
                    ? Array.Empty<IPublishedContentType>()
                    : CreateContentTypes(PublishedItemType.Media, refreshedIds.ToArray()).ToArray();

                var kits = refreshedIds == null
                    ? Array.Empty<ContentNodeKit>()
                    : _dataSource.GetTypeMediaSources(scope, refreshedIds).ToArray();

                _mediaStore.UpdateContentTypes(removedIds, typesA, kits);
                if (!otherIds.IsCollectionEmpty())
                    _mediaStore.UpdateContentTypes(CreateContentTypes(PublishedItemType.Media, otherIds.ToArray()).ToArray());
                if (!newIds.IsCollectionEmpty())
                    _mediaStore.NewContentTypes(CreateContentTypes(PublishedItemType.Media, newIds.ToArray()).ToArray());
                scope.Complete();
            }
        }

        #endregion

        #region Create, Get Published Snapshot

        private long _contentGen, _mediaGen, _domainGen;
        private IAppCache _elementsCache;

        public override IPublishedSnapshot CreatePublishedSnapshot(string previewToken)
        {
            // no cache, no joy
            if (_isReady == false)
                throw new InvalidOperationException("The published snapshot service has not properly initialized.");

            var preview = previewToken.IsNullOrWhiteSpace() == false;
            return new PublishedSnapshot(this, preview);
        }

        // gets a new set of elements
        // always creates a new set of elements,
        // even though the underlying elements may not change (store snapshots)
        public PublishedSnapshot.PublishedSnapshotElements GetElements(bool previewDefault)
        {
            // note: using ObjectCacheAppCache for elements and snapshot caches
            // is not recommended because it creates an inner MemoryCache which is a heavy
            // thing - better use a dictionary-based cache which "just" creates a concurrent
            // dictionary

            // for snapshot cache, DictionaryAppCache MAY be OK but it is not thread-safe,
            // nothing like that...
            // for elements cache, DictionaryAppCache is a No-No, use something better.
            // ie FastDictionaryAppCache (thread safe and all)

            ContentStore.Snapshot contentSnap, mediaSnap;
            SnapDictionary<int, Domain>.Snapshot domainSnap;
            IAppCache elementsCache;
            lock (_storesLock)
            {
                var scopeContext = _scopeProvider.Context;

                if (scopeContext == null)
                {
                    contentSnap = _contentStore.CreateSnapshot();
                    mediaSnap = _mediaStore.CreateSnapshot();
                    domainSnap = _domainStore.CreateSnapshot();
                    elementsCache = _elementsCache;
                }
                else
                {
                    contentSnap = _contentStore.LiveSnapshot;
                    mediaSnap = _mediaStore.LiveSnapshot;
                    domainSnap = _domainStore.Test.LiveSnapshot;
                    elementsCache = _elementsCache;

                    // this is tricky
                    // we are returning elements composed from live snapshots, which we need to replace
                    // with actual snapshots when the context is gone - but when the action runs, there
                    // still is a context - so we cannot get elements - just resync = nulls the current
                    // elements
                    // just need to make sure nothing gets elements in another enlisted action... so using
                    // a MaxValue to make sure this one runs last, and it should be ok
                    scopeContext.Enlist("Umbraco.Web.PublishedCache.NuCache.PublishedSnapshotService.Resync", () => this, (completed, svc) =>
                    {
                        ((PublishedSnapshot)svc.CurrentPublishedSnapshot)?.Resync();
                    }, int.MaxValue);
                }

                // create a new snapshot cache if snapshots are different gens
                if (contentSnap.Gen != _contentGen || mediaSnap.Gen != _mediaGen || domainSnap.Gen != _domainGen || _elementsCache == null)
                {
                    _contentGen = contentSnap.Gen;
                    _mediaGen = mediaSnap.Gen;
                    _domainGen = domainSnap.Gen;
                    elementsCache = _elementsCache = new FastDictionaryAppCache();
                }
            }

            var snapshotCache = new DictionaryAppCache();

            var memberTypeCache = new PublishedContentTypeCache(null, null, _serviceContext.MemberTypeService, _publishedContentTypeFactory, _logger);

            var defaultCulture = _defaultCultureAccessor.DefaultCulture;
            var domainCache = new DomainCache(domainSnap, defaultCulture);

            return new PublishedSnapshot.PublishedSnapshotElements
            {
                ContentCache = new ContentCache(previewDefault, contentSnap, snapshotCache, elementsCache, domainCache, _globalSettings, VariationContextAccessor),
                MediaCache = new MediaCache(previewDefault, mediaSnap, VariationContextAccessor),
                MemberCache = new MemberCache(previewDefault, snapshotCache, _serviceContext.MemberService, memberTypeCache, PublishedSnapshotAccessor, VariationContextAccessor, _entitySerializer),
                DomainCache = domainCache,
                SnapshotCache = snapshotCache,
                ElementsCache = elementsCache
            };
        }

        #endregion

        #region Preview

        public override string EnterPreview(IUser user, int contentId)
        {
            return "preview"; // anything
        }

        public override void RefreshPreview(string previewToken, int contentId)
        {
            // nothing
        }

        public override void ExitPreview(string previewToken)
        {
            // nothing
        }

        #endregion

        #region Handle Repository Events For Database PreCache

        // note: if the service is not ready, ie _isReady is false, then we still handle repository events,
        // because we can, we do not need a working published snapshot to do it - the only reason why it could cause an
        // issue is if the database table is not ready, but that should be prevented by migrations.

        // we need them to be "repository" events ie to trigger from within the repository transaction,
        // because they need to be consistent with the content that is being refreshed/removed - and that
        // should be guaranteed by a DB transaction

        private void OnContentRemovingEntity(DocumentRepository sender, DocumentRepository.ScopedEntityEventArgs args)
        {
            OnRemovedEntity(args.Scope.Database, args.Entity);
        }

        private void OnMediaRemovingEntity(MediaRepository sender, MediaRepository.ScopedEntityEventArgs args)
        {
            OnRemovedEntity(args.Scope.Database, args.Entity);
        }

        private void OnMemberRemovingEntity(MemberRepository sender, MemberRepository.ScopedEntityEventArgs args)
        {
            OnRemovedEntity(args.Scope.Database, args.Entity);
        }

        private void OnRemovedEntity(IUmbracoDatabase db, IContentBase item)
        {
            db.Execute("DELETE FROM cmsContentNu WHERE nodeId=@id", new { id = item.Id });
        }

        private void OnContentRefreshedEntity(DocumentRepository sender, DocumentRepository.ScopedEntityEventArgs args)
        {
            var db = args.Scope.Database;
            var content = (Content)args.Entity;

            // always refresh the edited data
            OnRepositoryRefreshed(db, content, false);

            // if unpublishing, remove published data from table
            if (content.PublishedState == PublishedState.Unpublishing)
                db.Execute("DELETE FROM cmsContentNu WHERE nodeId=@id AND published=1", new { id = content.Id });

            // if publishing, refresh the published data
            else if (content.PublishedState == PublishedState.Publishing)
                OnRepositoryRefreshed(db, content, true);
        }

        private void OnMediaRefreshedEntity(MediaRepository sender, MediaRepository.ScopedEntityEventArgs args)
        {
            var db = args.Scope.Database;
            var media = args.Entity;

            // refresh the edited data
            OnRepositoryRefreshed(db, media, false);
        }

        private void OnMemberRefreshedEntity(MemberRepository sender, MemberRepository.ScopedEntityEventArgs args)
        {
            var db = args.Scope.Database;
            var member = args.Entity;

            // refresh the edited data
            OnRepositoryRefreshed(db, member, true);
        }

        private void OnRepositoryRefreshed(IUmbracoDatabase db, IContentBase content, bool published)
        {
            // use a custom SQL to update row version on each update
            //db.InsertOrUpdate(dto);

            var dto = GetDto(content, published);
            db.InsertOrUpdate(dto,
                "SET data=@data, rv=rv+1 WHERE nodeId=@id AND published=@published",
                new
                {
                    data = dto.Data,
                    id = dto.NodeId,
                    published = dto.Published
                });
        }

        private void OnContentTypeRefreshedEntity(IContentTypeService sender, ContentTypeChange<IContentType>.EventArgs args)
        {
            const ContentTypeChangeTypes types // only for those that have been refreshed
                = ContentTypeChangeTypes.RefreshMain | ContentTypeChangeTypes.RefreshOther;
            var contentTypeIds = args.Changes.Where(x => x.ChangeTypes.HasTypesAny(types)).Select(x => x.Item.Id).ToArray();
            if (contentTypeIds.Any())
                RebuildContentDbCache(contentTypeIds: contentTypeIds);
        }

        private void OnMediaTypeRefreshedEntity(IMediaTypeService sender, ContentTypeChange<IMediaType>.EventArgs args)
        {
            const ContentTypeChangeTypes types // only for those that have been refreshed
                = ContentTypeChangeTypes.RefreshMain | ContentTypeChangeTypes.RefreshOther;
            var mediaTypeIds = args.Changes.Where(x => x.ChangeTypes.HasTypesAny(types)).Select(x => x.Item.Id).ToArray();
            if (mediaTypeIds.Any())
                RebuildMediaDbCache(contentTypeIds: mediaTypeIds);
        }

        private void OnMemberTypeRefreshedEntity(IMemberTypeService sender, ContentTypeChange<IMemberType>.EventArgs args)
        {
            const ContentTypeChangeTypes types // only for those that have been refreshed
                = ContentTypeChangeTypes.RefreshMain | ContentTypeChangeTypes.RefreshOther;
            var memberTypeIds = args.Changes.Where(x => x.ChangeTypes.HasTypesAny(types)).Select(x => x.Item.Id).ToArray();
            if (memberTypeIds.Any())
                RebuildMemberDbCache(contentTypeIds: memberTypeIds);
        }

        private ContentNuDto GetDto(IContentBase content, bool published)
        {
            // should inject these in ctor
            // BUT for the time being we decide not to support ConvertDbToXml/String
            //var propertyEditorResolver = PropertyEditorResolver.Current;
            //var dataTypeService = ApplicationContext.Current.Services.DataTypeService;

            var propertyData = new Dictionary<string, PropertyData[]>();
            foreach (var prop in content.Properties)
            {
                var pdatas = new List<PropertyData>();
                foreach (var pvalue in prop.Values)
                {
                    // sanitize - properties should be ok but ... never knows
                    if (!prop.PropertyType.SupportsVariation(pvalue.Culture, pvalue.Segment))
                        continue;

                    // note: at service level, invariant is 'null', but here invariant becomes 'string.Empty'
                    var value = published ? pvalue.PublishedValue : pvalue.EditedValue;
                    if (value != null)
                        pdatas.Add(new PropertyData { Culture = pvalue.Culture ?? string.Empty, Segment = pvalue.Segment ?? string.Empty, Value = value });

                    //Core.Composing.Current.Logger.Debug<PublishedSnapshotService>($"{content.Id} {prop.Alias} [{pvalue.LanguageId},{pvalue.Segment}] {value} {(published?"pub":"edit")}");

                    //if (value != null)
                    //{
                    //    var e = propertyEditorResolver.GetByAlias(prop.PropertyType.PropertyEditorAlias);

                    //    // We are converting to string, even for database values which are integer or
                    //    // DateTime, which is not optimum. Doing differently would require that we have a way to tell
                    //    // whether the conversion to XML string changes something or not... which we don't, and we
                    //    // don't want to implement it as PropertyValueEditor.ConvertDbToXml/String should die anyway.

                    //    // Don't think about improving the situation here: this is a corner case and the real
                    //    // thing to do is to get rig of PropertyValueEditor.ConvertDbToXml/String.

                    //    // Use ConvertDbToString to keep it simple, although everywhere we use ConvertDbToXml and
                    //    // nothing ensures that the two methods are consistent.

                    //    if (e != null)
                    //        value = e.ValueEditor.ConvertDbToString(prop, prop.PropertyType, dataTypeService);
                    //}
                }
                propertyData[prop.Alias] = pdatas.ToArray();
            }

            var cultureData = new Dictionary<string, CultureVariation>();

            // sanitize - names should be ok but ... never knows
            if (content.ContentType.VariesByCulture())
            {
                var infos = content is IContent document
                    ? (published
                        ? document.PublishCultureInfos
                        : document.CultureInfos)
                    : content.CultureInfos;

                // ReSharper disable once UseDeconstruction
                foreach (var cultureInfo in infos)
                {
                    var cultureIsDraft = !published && content is IContent d && d.IsCultureEdited(cultureInfo.Culture);
                    cultureData[cultureInfo.Culture] = new CultureVariation
                    {
                        Name = cultureInfo.Name,
                        UrlSegment = content.GetUrlSegment(_urlSegmentProviders, cultureInfo.Culture),
                        Date = content.GetUpdateDate(cultureInfo.Culture) ?? DateTime.MinValue,
                        IsDraft = cultureIsDraft
                    };
                }
            }

            //the dictionary that will be serialized
            var nestedData = new ContentNestedData
            {
                PropertyData = propertyData,
                CultureData = cultureData,
                UrlSegment = content.GetUrlSegment(_urlSegmentProviders)
            };

            var dto = new ContentNuDto
            {
                NodeId = content.Id,
                Published = published,

                // note that numeric values (which are Int32) are serialized without their
                // type (eg "value":1234) and JsonConvert by default deserializes them as Int64

                Data = JsonConvert.SerializeObject(nestedData)
            };

            //Core.Composing.Current.Logger.Debug<PublishedSnapshotService>(dto.Data);

            return dto;
        }

        #endregion

        #region Rebuild Database PreCache

        public override void Rebuild()
        {
            _logger.Debug<PublishedSnapshotService>("Rebuilding...");
            using (var scope = _scopeProvider.CreateScope(repositoryCacheMode: RepositoryCacheMode.Scoped))
            {
                scope.ReadLock(Constants.Locks.ContentTree);
                scope.ReadLock(Constants.Locks.MediaTree);
                scope.ReadLock(Constants.Locks.MemberTree);
                RebuildContentDbCacheLocked(scope, 5000, null);
                RebuildMediaDbCacheLocked(scope, 5000, null);
                RebuildMemberDbCacheLocked(scope, 5000, null);
                scope.Complete();
            }
        }

        public void RebuildContentDbCache(int groupSize = 5000, IEnumerable<int> contentTypeIds = null)
        {
            using (var scope = _scopeProvider.CreateScope(repositoryCacheMode: RepositoryCacheMode.Scoped))
            {
                scope.ReadLock(Constants.Locks.ContentTree);
                RebuildContentDbCacheLocked(scope, groupSize, contentTypeIds);
                scope.Complete();
            }
        }

        // assumes content tree lock
        private void RebuildContentDbCacheLocked(IScope scope, int groupSize, IEnumerable<int> contentTypeIds)
        {
            var contentTypeIdsA = contentTypeIds?.ToArray();
            var contentObjectType = Constants.ObjectTypes.Document;
            var db = scope.Database;

            // remove all - if anything fails the transaction will rollback
            if (contentTypeIds == null || contentTypeIdsA.Length == 0)
            {
                // must support SQL-CE
                db.Execute(@"DELETE FROM cmsContentNu
WHERE cmsContentNu.nodeId IN (
    SELECT id FROM umbracoNode WHERE umbracoNode.nodeObjectType=@objType
)",
                    new { objType = contentObjectType });
            }
            else
            {
                // assume number of ctypes won't blow IN(...)
                // must support SQL-CE
                db.Execute($@"DELETE FROM cmsContentNu
WHERE cmsContentNu.nodeId IN (
    SELECT id FROM umbracoNode
    JOIN {Constants.DatabaseSchema.Tables.Content} ON {Constants.DatabaseSchema.Tables.Content}.nodeId=umbracoNode.id
    WHERE umbracoNode.nodeObjectType=@objType
    AND {Constants.DatabaseSchema.Tables.Content}.contentTypeId IN (@ctypes)
)",
                    new { objType = contentObjectType, ctypes = contentTypeIdsA });
            }

            // insert back - if anything fails the transaction will rollback
            var query = scope.SqlContext.Query<IContent>();
            if (contentTypeIds != null && contentTypeIdsA.Length > 0)
                query = query.WhereIn(x => x.ContentTypeId, contentTypeIdsA); // assume number of ctypes won't blow IN(...)

            long pageIndex = 0;
            long processed = 0;
            long total;
            do
            {
                // the tree is locked, counting and comparing to total is safe
                var descendants = _documentRepository.GetPage(query, pageIndex++, groupSize, out total, null, Ordering.By("Path"));
                var items = new List<ContentNuDto>();
                var count = 0;
                foreach (var c in descendants)
                {
                    // always the edited version
                    items.Add(GetDto(c, false));

                    // and also the published version if it makes any sense
                    if (c.Published)
                        items.Add(GetDto(c, true));

                    count++;
                }

                db.BulkInsertRecords(items);
                processed += count;
            } while (processed < total);
        }

        public void RebuildMediaDbCache(int groupSize = 5000, IEnumerable<int> contentTypeIds = null)
        {
            using (var scope = _scopeProvider.CreateScope(repositoryCacheMode: RepositoryCacheMode.Scoped))
            {
                scope.ReadLock(Constants.Locks.MediaTree);
                RebuildMediaDbCacheLocked(scope, groupSize, contentTypeIds);
                scope.Complete();
            }
        }

        // assumes media tree lock
        public void RebuildMediaDbCacheLocked(IScope scope, int groupSize, IEnumerable<int> contentTypeIds)
        {
            var contentTypeIdsA = contentTypeIds?.ToArray();
            var mediaObjectType = Constants.ObjectTypes.Media;
            var db = scope.Database;

            // remove all - if anything fails the transaction will rollback
            if (contentTypeIds == null || contentTypeIdsA.Length == 0)
            {
                // must support SQL-CE
                db.Execute(@"DELETE FROM cmsContentNu
WHERE cmsContentNu.nodeId IN (
    SELECT id FROM umbracoNode WHERE umbracoNode.nodeObjectType=@objType
)",
                    new { objType = mediaObjectType });
            }
            else
            {
                // assume number of ctypes won't blow IN(...)
                // must support SQL-CE
                db.Execute($@"DELETE FROM cmsContentNu
WHERE cmsContentNu.nodeId IN (
    SELECT id FROM umbracoNode
    JOIN {Constants.DatabaseSchema.Tables.Content} ON {Constants.DatabaseSchema.Tables.Content}.nodeId=umbracoNode.id
    WHERE umbracoNode.nodeObjectType=@objType
    AND {Constants.DatabaseSchema.Tables.Content}.contentTypeId IN (@ctypes)
)",
                    new { objType = mediaObjectType, ctypes = contentTypeIdsA });
            }

            // insert back - if anything fails the transaction will rollback
            var query = scope.SqlContext.Query<IMedia>();
            if (contentTypeIds != null && contentTypeIdsA.Length > 0)
                query = query.WhereIn(x => x.ContentTypeId, contentTypeIdsA); // assume number of ctypes won't blow IN(...)

            long pageIndex = 0;
            long processed = 0;
            long total;
            do
            {
                // the tree is locked, counting and comparing to total is safe
                var descendants = _mediaRepository.GetPage(query, pageIndex++, groupSize, out total, null, Ordering.By("Path"));
                var items = descendants.Select(m => GetDto(m, false)).ToList();
                db.BulkInsertRecords(items);
                processed += items.Count;
            } while (processed < total);
        }

        public void RebuildMemberDbCache(int groupSize = 5000, IEnumerable<int> contentTypeIds = null)
        {
            using (var scope = _scopeProvider.CreateScope(repositoryCacheMode: RepositoryCacheMode.Scoped))
            {
                scope.ReadLock(Constants.Locks.MemberTree);
                RebuildMemberDbCacheLocked(scope, groupSize, contentTypeIds);
                scope.Complete();
            }
        }

        // assumes member tree lock
        public void RebuildMemberDbCacheLocked(IScope scope, int groupSize, IEnumerable<int> contentTypeIds)
        {
            var contentTypeIdsA = contentTypeIds?.ToArray();
            var memberObjectType = Constants.ObjectTypes.Member;
            var db = scope.Database;

            // remove all - if anything fails the transaction will rollback
            if (contentTypeIds == null || contentTypeIdsA.Length == 0)
            {
                // must support SQL-CE
                db.Execute(@"DELETE FROM cmsContentNu
WHERE cmsContentNu.nodeId IN (
    SELECT id FROM umbracoNode WHERE umbracoNode.nodeObjectType=@objType
)",
                    new { objType = memberObjectType });
            }
            else
            {
                // assume number of ctypes won't blow IN(...)
                // must support SQL-CE
                db.Execute($@"DELETE FROM cmsContentNu
WHERE cmsContentNu.nodeId IN (
    SELECT id FROM umbracoNode
    JOIN {Constants.DatabaseSchema.Tables.Content} ON {Constants.DatabaseSchema.Tables.Content}.nodeId=umbracoNode.id
    WHERE umbracoNode.nodeObjectType=@objType
    AND {Constants.DatabaseSchema.Tables.Content}.contentTypeId IN (@ctypes)
)",
                    new { objType = memberObjectType, ctypes = contentTypeIdsA });
            }

            // insert back - if anything fails the transaction will rollback
            var query = scope.SqlContext.Query<IMember>();
            if (contentTypeIds != null && contentTypeIdsA.Length > 0)
                query = query.WhereIn(x => x.ContentTypeId, contentTypeIdsA); // assume number of ctypes won't blow IN(...)

            long pageIndex = 0;
            long processed = 0;
            long total;
            do
            {
                var descendants = _memberRepository.GetPage(query, pageIndex++, groupSize, out total, null, Ordering.By("Path"));
                var items = descendants.Select(m => GetDto(m, false)).ToArray();
                db.BulkInsertRecords(items);
                processed += items.Length;
            } while (processed < total);
        }

        public bool VerifyContentDbCache()
        {
            using (var scope = _scopeProvider.CreateScope())
            {
                scope.ReadLock(Constants.Locks.ContentTree);
                var ok = VerifyContentDbCacheLocked(scope);
                scope.Complete();
                return ok;
            }
        }

        // assumes content tree lock
        private bool VerifyContentDbCacheLocked(IScope scope)
        {
            // every document should have a corresponding row for edited properties
            // and if published, may have a corresponding row for published properties

            var contentObjectType = Constants.ObjectTypes.Document;
            var db = scope.Database;

            var count = db.ExecuteScalar<int>($@"SELECT COUNT(*)
FROM umbracoNode
JOIN {Constants.DatabaseSchema.Tables.Document} ON umbracoNode.id={Constants.DatabaseSchema.Tables.Document}.nodeId
LEFT JOIN cmsContentNu nuEdited ON (umbracoNode.id=nuEdited.nodeId AND nuEdited.published=0)
LEFT JOIN cmsContentNu nuPublished ON (umbracoNode.id=nuPublished.nodeId AND nuPublished.published=1)
WHERE umbracoNode.nodeObjectType=@objType
AND nuEdited.nodeId IS NULL OR ({Constants.DatabaseSchema.Tables.Document}.published=1 AND nuPublished.nodeId IS NULL);"
                , new { objType = contentObjectType });

            return count == 0;
        }

        public bool VerifyMediaDbCache()
        {
            using (var scope = _scopeProvider.CreateScope())
            {
                scope.ReadLock(Constants.Locks.MediaTree);
                var ok = VerifyMediaDbCacheLocked(scope);
                scope.Complete();
                return ok;
            }
        }

        // assumes media tree lock
        public bool VerifyMediaDbCacheLocked(IScope scope)
        {
            // every media item should have a corresponding row for edited properties

            var mediaObjectType = Constants.ObjectTypes.Media;
            var db = scope.Database;

            var count = db.ExecuteScalar<int>(@"SELECT COUNT(*)
FROM umbracoNode
LEFT JOIN cmsContentNu ON (umbracoNode.id=cmsContentNu.nodeId AND cmsContentNu.published=0)
WHERE umbracoNode.nodeObjectType=@objType
AND cmsContentNu.nodeId IS NULL
", new { objType = mediaObjectType });

            return count == 0;
        }

        public bool VerifyMemberDbCache()
        {
            using (var scope = _scopeProvider.CreateScope())
            {
                scope.ReadLock(Constants.Locks.MemberTree);
                var ok = VerifyMemberDbCacheLocked(scope);
                scope.Complete();
                return ok;
            }
        }

        // assumes member tree lock
        public bool VerifyMemberDbCacheLocked(IScope scope)
        {
            // every member item should have a corresponding row for edited properties

            var memberObjectType = Constants.ObjectTypes.Member;
            var db = scope.Database;

            var count = db.ExecuteScalar<int>(@"SELECT COUNT(*)
FROM umbracoNode
LEFT JOIN cmsContentNu ON (umbracoNode.id=cmsContentNu.nodeId AND cmsContentNu.published=0)
WHERE umbracoNode.nodeObjectType=@objType
AND cmsContentNu.nodeId IS NULL
", new { objType = memberObjectType });

            return count == 0;
        }

        #endregion

        #region Instrument

        public string GetStatus()
        {
            var dbCacheIsOk = VerifyContentDbCache()
                && VerifyMediaDbCache()
                && VerifyMemberDbCache();

            var cg = _contentStore.GenCount;
            var mg = _mediaStore.GenCount;
            var cs = _contentStore.SnapCount;
            var ms = _mediaStore.SnapCount;
            var ce = _contentStore.Count;
            var me = _mediaStore.Count;

            return
                " Database cache is " + (dbCacheIsOk ? "ok" : "NOT ok (rebuild?)") + "." +
                " ContentStore contains " + ce + " item" + (ce > 1 ? "s" : "") +
                " and has " + cg + " generation" + (cg > 1 ? "s" : "") +
                " and " + cs + " snapshot" + (cs > 1 ? "s" : "") + "." +
                " MediaStore contains " + me + " item" + (ce > 1 ? "s" : "") +
                " and has " + mg + " generation" + (mg > 1 ? "s" : "") +
                " and " + ms + " snapshot" + (ms > 1 ? "s" : "") + ".";
        }

        public void Collect()
        {
            var contentCollect = _contentStore.CollectAsync();
            var mediaCollect = _mediaStore.CollectAsync();
            System.Threading.Tasks.Task.WaitAll(contentCollect, mediaCollect);
        }

        #endregion
    }
}
