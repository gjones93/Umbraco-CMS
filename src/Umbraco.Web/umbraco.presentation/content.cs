using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using umbraco.BusinessLogic;
using umbraco.cms.businesslogic;
using umbraco.cms.businesslogic.web;
using umbraco.DataLayer;
using Umbraco.Core;
using Umbraco.Core.Cache;
using Umbraco.Core.Configuration;
using Umbraco.Core.IO;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Profiling;
using Umbraco.Core.Services;
using Umbraco.Core.Strings;
using Umbraco.Web;
using Umbraco.Web.PublishedCache.XmlPublishedCache;
using Umbraco.Web.Scheduling;
using File = System.IO.File;
using Task = System.Threading.Tasks.Task;

namespace umbraco
{
    /// <summary>
    /// Represents the Xml storage for the Xml published cache.
    /// </summary>
    public class content
    {
        private XmlCacheFilePersister _persisterTask;

        private volatile bool _released;

        #region Constructors

        private content()
        {
            if (SyncToXmlFile)
            {
                var logger = LoggerResolver.HasCurrent ? LoggerResolver.Current.Logger : new DebugDiagnosticsLogger();
                var profingLogger = new ProfilingLogger(
                    logger,
                    ProfilerResolver.HasCurrent ? ProfilerResolver.Current.Profiler : new LogProfiler(logger));
 
                // prepare the persister task
                // there's always be one task keeping a ref to the runner
                // so it's safe to just create it as a local var here
                var runner = new BackgroundTaskRunner<XmlCacheFilePersister>("XmlCacheFilePersister", new BackgroundTaskRunnerOptions
                {
                    LongRunning = true,
                    KeepAlive = true,
                    Hosted = false // main domain will take care of stopping the runner (see below)
                }, logger);

                // create (and add to runner)
                _persisterTask = new XmlCacheFilePersister(runner, this, profingLogger);

                var registered = ApplicationContext.Current.MainDom.Register(
                    null,
                    () =>
                    {
                        // once released, the cache still works but does not write to file anymore,
                        // which is OK with database server messenger but will cause data loss with
                        // another messenger...
                        
                        runner.Shutdown(false, true); // wait until flushed
                        _released = true;
                    });

                // failed to become the main domain, we will never use the file
                if (registered == false)
                    runner.Shutdown(false, true);

                _released = (registered == false);
            }

            // initialize content - populate the cache
            using (var safeXml = GetSafeXmlWriter(false))
            {
                bool registerXmlChange;

                // if we don't use the file then LoadXmlLocked will not even
                // read from the file and will go straight to database
                LoadXmlLocked(safeXml, out registerXmlChange);
                // if we use the file and registerXmlChange is true this will
                // write to file, else it will not
                safeXml.Commit(registerXmlChange);
            }
        }

        #endregion

        #region Singleton

        private static readonly Lazy<content> LazyInstance = new Lazy<content>(() => new content());

        public static content Instance
        {
            get
            {
                return LazyInstance.Value;
            }
        }

        #endregion

        #region Legacy & Stuff

        // sync database access
        // (not refactoring that part at the moment)
        private static readonly object DbReadSyncLock = new object();

        private const string XmlContextContentItemKey = "UmbracoXmlContextContent";
        private static string _umbracoXmlDiskCacheFileName = string.Empty;
        private volatile XmlDocument _xmlContent;

        /// <summary>
        /// Gets the path of the umbraco XML disk cache file.
        /// </summary>
        /// <value>The name of the umbraco XML disk cache file.</value>
        public static string GetUmbracoXmlDiskFileName()
        {
            if (string.IsNullOrEmpty(_umbracoXmlDiskCacheFileName))
            {
                _umbracoXmlDiskCacheFileName = IOHelper.MapPath(SystemFiles.ContentCacheXml);
            }
            return _umbracoXmlDiskCacheFileName;
        }

        [Obsolete("Use the safer static GetUmbracoXmlDiskFileName() method instead to retrieve this value")]
        public string UmbracoXmlDiskCacheFileName
        {
            get { return GetUmbracoXmlDiskFileName(); }
            set { _umbracoXmlDiskCacheFileName = value; }
        }

        //NOTE: We CANNOT use this for a double check lock because it is a property, not a field and to do double
        // check locking in c# you MUST have a volatile field. Even thoug this wraps a volatile field it will still 
        // not work as expected for a double check lock because properties are treated differently in the clr.
        public virtual bool isInitializing
        {
            get { return _xmlContent == null; }
        }
        
        #endregion

        #region Public Methods

        /// <summary>
        /// Load content from database and replaces active content when done.
        /// </summary>
        public virtual void RefreshContentFromDatabase()
        {
            using (var safeXml = GetSafeXmlWriter())
            {
                safeXml.Xml = LoadContentFromDatabase();
            }
        }

        /// <summary>
        /// Used by all overloaded publish methods to do the actual "noderepresentation to xml"
        /// </summary>
        /// <param name="d"></param>
        /// <param name="xmlContentCopy"></param>
        /// <param name="updateSitemapProvider"></param>
        public static XmlDocument PublishNodeDo(Document d, XmlDocument xmlContentCopy, bool updateSitemapProvider)
        {
            // check if document *is* published, it could be unpublished by an event
            if (d.Published)
            {
                var parentId = d.Level == 1 ? -1 : d.ParentId;

                // fix sortOrder - see note in UpdateSortOrder
                var node = GetPreviewOrPublishedNode(d, xmlContentCopy, false);
                var attr = ((XmlElement)node).GetAttributeNode("sortOrder");
                attr.Value = d.sortOrder.ToString();
                xmlContentCopy = GetAddOrUpdateXmlNode(xmlContentCopy, d.Id, d.Level, parentId, node);
                
            }

            return xmlContentCopy;
        }

        private static XmlNode GetPreviewOrPublishedNode(Document d, XmlDocument xmlContentCopy, bool isPreview)
        {
            var contentItem = d.ContentEntity;
            var services = ApplicationContext.Current.Services;

            if (isPreview)
            {
                var xml = services.ContentService.GetContentPreviewXml(contentItem.Id, contentItem.Version);
                return xml.GetXmlNode(xmlContentCopy);
            }
            else
            {
                var xml = services.ContentService.GetContentXml(contentItem.Id);
                return xml.GetXmlNode(xmlContentCopy);
            }
        }

        /// <summary>
        /// Sorts the documents.
        /// </summary>
        /// <param name="parentId">The parent node identifier.</param>
        public void SortNodes(int parentId)
        {
            var childNodesXPath = "./* [@id]";

            using (var safeXml = GetSafeXmlWriter(false))
            {
                var parentNode = parentId == -1
                    ? safeXml.Xml.DocumentElement
                    : safeXml.Xml.GetElementById(parentId.ToString(CultureInfo.InvariantCulture));

                if (parentNode == null) return;

                var sorted = XmlHelper.SortNodesIfNeeded(
                    parentNode,
                    childNodesXPath,
                    x => x.AttributeValue<int>("sortOrder"));

                if (sorted == false) return;

                safeXml.Commit();
            }
        }

        /// <summary>
        /// Updates the document cache.
        /// </summary>
        /// <param name="pageId">The page id.</param>
        public virtual void UpdateDocumentCache(int pageId)
        {
            var d = new Document(pageId);
            UpdateDocumentCache(d);
        }

        /// <summary>
        /// Updates the document cache.
        /// </summary>
        /// <param name="d">The d.</param>
        public virtual void UpdateDocumentCache(Document d)
        {
            var e = new DocumentCacheEventArgs();

            // lock the xml cache so no other thread can write to it at the same time
            // note that some threads could read from it while we hold the lock, though
            using (var safeXml = GetSafeXmlWriter())
            {
                safeXml.Xml = PublishNodeDo(d, safeXml.Xml, true);
            }

            ClearContextCache();

            var cachedFieldKeyStart = string.Format("{0}{1}_", CacheKeys.ContentItemCacheKey, d.Id);
            ApplicationContext.Current.ApplicationCache.RuntimeCache.ClearCacheByKeySearch(cachedFieldKeyStart);

            FireAfterUpdateDocumentCache(d, e);
        }

        internal virtual void UpdateSortOrder(int contentId)
        {
            var content = ApplicationContext.Current.Services.ContentService.GetById(contentId);
            if (content == null) return;
            UpdateSortOrder(content);
        }

        internal virtual void UpdateSortOrder(IContent c)
        {
            if (c == null) throw new ArgumentNullException("c");

            // the XML in database is updated only when content is published, and then
            // it contains the sortOrder value at the time the XML was generated. when
            // a document with unpublished changes is sorted, then it is simply saved
            // (see ContentService) and so the sortOrder has changed but the XML has
            // not been updated accordingly.

            // this updates the published cache to take care of the situation
            // without ContentService having to ... what exactly?

            // no need to do it if the content is published without unpublished changes,
            // though, because in that case the XML will get re-generated with the
            // correct sort order.
            if (c.Published)
                return;

            using (var safeXml = GetSafeXmlWriter(false))
            {
                var node = safeXml.Xml.GetElementById(c.Id.ToString(CultureInfo.InvariantCulture));
                if (node == null) return;
                var attr = node.GetAttributeNode("sortOrder");
                if (attr == null) return;
                var sortOrder = c.SortOrder.ToString(CultureInfo.InvariantCulture);
                if (attr.Value == sortOrder) return;

                // only if node was actually modified
                attr.Value = sortOrder;

                safeXml.Commit();
            }
        }

        /// <summary>
        /// Updates the document cache for multiple documents
        /// </summary>
        /// <param name="Documents">The documents.</param>
        [Obsolete("This is not used and will be removed from the codebase in future versions")]
        public virtual void UpdateDocumentCache(List<Document> Documents)
        {
            // We need to lock content cache here, because we cannot allow other threads
            // making changes at the same time, they need to be queued
            int parentid = Documents[0].Id;


            using (var safeXml = GetSafeXmlWriter())
            {
                foreach (Document d in Documents)
                {
                    safeXml.Xml = PublishNodeDo(d, safeXml.Xml, true);
                }
            }

            ClearContextCache();
        }
        
        public virtual void ClearDocumentCache(int documentId)
        {
            var e = new DocumentCacheEventArgs();
            // Get the document
            Document d;
            try
            {
                d = new Document(documentId);
            }
            catch
            {
                // if we need the document to remove it... this cannot be LB?!
                // shortcut everything here
                ClearDocumentXmlCache(documentId);
                return;
            }
            ClearDocumentCache(d);
            FireAfterClearDocumentCache(d, e);
        }

        /// <summary>
        /// Clears the document cache and removes the document from the xml db cache.
        /// This means the node gets unpublished from the website.
        /// </summary>
        /// <param name="doc">The document</param>
        internal void ClearDocumentCache(Document doc)
        {
            var e = new DocumentCacheEventArgs();
            XmlNode x;

            // remove from xml db cache 
            doc.XmlRemoveFromDB();

            // clear xml cache
            ClearDocumentXmlCache(doc.Id);

            ClearContextCache();

            FireAfterClearDocumentCache(doc, e);
        }

        internal void ClearDocumentXmlCache(int id)
        {
            // We need to lock content cache here, because we cannot allow other threads
            // making changes at the same time, they need to be queued
            using (var safeXml = GetSafeXmlReader())
            {
                // Check if node present, before cloning
                var x = safeXml.Xml.GetElementById(id.ToString());
                if (x == null)
                    return;

                safeXml.UpgradeToWriter(false);

                // Find the document in the xml cache
                x = safeXml.Xml.GetElementById(id.ToString());
                if (x != null)
                {
                    // The document already exists in cache, so repopulate it
                    x.ParentNode.RemoveChild(x);
                    safeXml.Commit();
                }
            }
        }

        /// <summary>
        /// Unpublishes the  node.
        /// </summary>
        /// <param name="documentId">The document id.</param>
        [Obsolete("Please use: umbraco.content.ClearDocumentCache", true)]
        public virtual void UnPublishNode(int documentId)
        {
            ClearDocumentCache(documentId);
        }

        #endregion

        #region Protected & Private methods

        /// <summary>
        /// Clear HTTPContext cache if any
        /// </summary>
        private void ClearContextCache()
        {
            // If running in a context very important to reset context cache orelse new nodes are missing
            if (UmbracoContext.Current != null && UmbracoContext.Current.HttpContext != null && UmbracoContext.Current.HttpContext.Items.Contains(XmlContextContentItemKey))
                UmbracoContext.Current.HttpContext.Items.Remove(XmlContextContentItemKey);
        }

        /// <summary>
        /// Load content from database
        /// </summary>
        private XmlDocument LoadContentFromDatabase()
        {
            try
            {
                // Try to log to the DB
                LogHelper.Info<content>("Loading content from database...");

                var hierarchy = new Dictionary<int, List<int>>();
                var nodeIndex = new Dictionary<int, XmlNode>();

                try
                {
                    LogHelper.Debug<content>("Republishing starting");

                    lock (DbReadSyncLock)
                    {

                        // Lets cache the DTD to save on the DB hit on the subsequent use
                        string dtd = ApplicationContext.Current.Services.ContentTypeService.GetDtd();

                        // Prepare an XmlDocument with an appropriate inline DTD to match
                        // the expected content
                        var xmlDoc = new XmlDocument();
                        InitializeXml(xmlDoc, dtd);

                        // Esben Carlsen: At some point we really need to put all data access into to a tier of its own.
                        // CLN - added checks that document xml is for a document that is actually published.
                        string sql =
                            @"select umbracoNode.id, umbracoNode.parentId, umbracoNode.sortOrder, cmsContentXml.xml from umbracoNode 
inner join cmsContentXml on cmsContentXml.nodeId = umbracoNode.id and umbracoNode.nodeObjectType = @type
where umbracoNode.id in (select cmsDocument.nodeId from cmsDocument where cmsDocument.published = 1)
order by umbracoNode.level, umbracoNode.sortOrder";


                        foreach (var dr in ApplicationContext.Current.DatabaseContext.Database.Query<dynamic>(sql, new { type = new Guid(Constants.ObjectTypes.Document)}))
                        {
                            int currentId = dr.id;
                            int parentId = dr.parentId;
                            string xml = dr.xml;

                            // fix sortOrder - see notes in UpdateSortOrder
                            var tmp = new XmlDocument();
                            tmp.LoadXml(xml);
                            var attr = tmp.DocumentElement.GetAttributeNode("sortOrder");
                            attr.Value = dr.sortOrder.ToString();
                            xml = tmp.InnerXml;

                            // check if a listener has canceled the event
                            // and parse it into a DOM node
                            xmlDoc.LoadXml(xml);
                            XmlNode node = xmlDoc.FirstChild;
                            nodeIndex.Add(currentId, node);

                            // verify if either of the handlers canceled the children to load
                            // Build the content hierarchy
                            List<int> children;
                            if (!hierarchy.TryGetValue(parentId, out children))
                            {
                                // No children for this parent, so add one
                                children = new List<int>();
                                hierarchy.Add(parentId, children);
                            }
                            children.Add(currentId);
                        }

                        LogHelper.Debug<content>("Xml Pages loaded");

                        try
                        {
                            // If we got to here we must have successfully retrieved the content from the DB so
                            // we can safely initialise and compose the final content DOM. 
                            // Note: We are reusing the XmlDocument used to create the xml nodes above so 
                            // we don't have to import them into a new XmlDocument

                            // Initialise the document ready for the final composition of content
                            InitializeXml(xmlDoc, dtd);

                            // Start building the content tree recursively from the root (-1) node
                            GenerateXmlDocument(hierarchy, nodeIndex, -1, xmlDoc.DocumentElement);

                            LogHelper.Debug<content>("Done republishing Xml Index");

                            return xmlDoc;
                        }
                        catch (Exception ee)
                        {
                            LogHelper.Error<content>("Error while generating XmlDocument from database", ee);
                        }
                    }
                }
                catch (OutOfMemoryException ee)
                {
                    LogHelper.Error<content>(string.Format("Error Republishing: Out Of Memory. Parents: {0}, Nodes: {1}", hierarchy.Count, nodeIndex.Count), ee);
                }
                catch (Exception ee)
                {
                    LogHelper.Error<content>("Error Republishing", ee);
                }
            }
            catch (Exception ee)
            {
                LogHelper.Error<content>("Error Republishing", ee);
            }

            // An error of some sort must have stopped us from successfully generating
            // the content tree, so lets return null signifying there is no content available
            return null;
        }

        private static void GenerateXmlDocument(IDictionary<int, List<int>> hierarchy,
                                                IDictionary<int, XmlNode> nodeIndex, int parentId, XmlNode parentNode)
        {
            List<int> children;

            if (hierarchy.TryGetValue(parentId, out children))
            {
                XmlNode childContainer = parentNode;
                

                foreach (int childId in children)
                {
                    XmlNode childNode = nodeIndex[childId];

                    parentNode.AppendChild(childNode);

                    // Recursively build the content tree under the current child
                    GenerateXmlDocument(hierarchy, nodeIndex, childId, childNode);
                }
            }
        }

      
        #endregion

        #region Configuration

        // gathering configuration options here to document what they mean

        private readonly bool _xmlFileEnabled = true;

        // whether the disk cache is enabled
        private bool XmlFileEnabled
        {
            get { return _xmlFileEnabled && UmbracoConfig.For.UmbracoSettings().Content.XmlCacheEnabled; }
        }

        // whether the disk cache is enabled and to update the disk cache when xml changes
        private bool SyncToXmlFile
        {
            get { return XmlFileEnabled && UmbracoConfig.For.UmbracoSettings().Content.ContinouslyUpdateXmlDiskCache; }
        }

        // whether the disk cache is enabled and to reload from disk cache if it changes
        private bool SyncFromXmlFile
        {
            get { return XmlFileEnabled && UmbracoConfig.For.UmbracoSettings().Content.XmlContentCheckForDiskChanges; }
        }
        

        // whether to keep version of everything (incl. medias & members) in cmsPreviewXml
        // for audit purposes - false by default, not in umbracoSettings.config
        // whether to... no idea what that one does
        // it is false by default and not in UmbracoSettings.config anymore - ignoring
        /*
        private static bool GlobalPreviewStorageEnabled
        {
            get { return UmbracoConfig.For.UmbracoSettings().Content.GlobalPreviewStorageEnabled; }
        }
        */

        // ensures config is valid

        #endregion

        #region Xml

        private readonly AsyncLock _xmlLock = new AsyncLock(); // protects _xml

        /// <remarks>
        /// Get content. First call to this property will initialize xmldoc
        /// subsequent calls will be blocked until initialization is done
        /// Further we cache (in context) xmlContent for each request to ensure that
        /// we always have the same XmlDoc throughout the whole request.
        /// </remarks>
        public virtual XmlDocument XmlContent
        {
            get
            {
                if (UmbracoContext.Current == null || UmbracoContext.Current.HttpContext == null)
                    return XmlContentInternal;
                var content = UmbracoContext.Current.HttpContext.Items[XmlContextContentItemKey] as XmlDocument;
                if (content == null)
                {
                    content = XmlContentInternal;
                    UmbracoContext.Current.HttpContext.Items[XmlContextContentItemKey] = content;
                }
                return content;
            }
        }

        [Obsolete("Please use: content.Instance.XmlContent")]
        public static XmlDocument xmlContent
        {
            get { return Instance.XmlContent; }
        }

        // to be used by content.Instance
        protected internal virtual XmlDocument XmlContentInternal
        {
            get
            {
                ReloadXmlFromFileIfChanged();
                return _xmlContent;
            }
        }

        // assumes xml lock
        private void SetXmlLocked(XmlDocument xml, bool registerXmlChange)
        {
            // this is the ONLY place where we write to _xmlContent
            _xmlContent = xml;

            if (registerXmlChange == false || SyncToXmlFile == false)
                return;

            //_lastXmlChange = DateTime.UtcNow;
            _persisterTask = _persisterTask.Touch(); // _persisterTask != null because SyncToXmlFile == true
        }

        private static XmlDocument Clone(XmlDocument xmlDoc)
        {
            return xmlDoc == null ? null : (XmlDocument)xmlDoc.CloneNode(true);
        }

        private static XmlDocument EnsureSchema(string contentTypeAlias, XmlDocument xml)
        {
            string subset = null;

            // get current doctype
            var n = xml.FirstChild;
            while (n.NodeType != XmlNodeType.DocumentType && n.NextSibling != null)
                n = n.NextSibling;
            if (n.NodeType == XmlNodeType.DocumentType)
                subset = ((XmlDocumentType)n).InternalSubset;

            // ensure it contains the content type
            if (subset != null && subset.Contains(string.Format("<!ATTLIST {0} id ID #REQUIRED>", contentTypeAlias)))
                return xml;

            // alas, that does not work, replacing a doctype is ignored and GetElementById fails
            //
            //// remove current doctype, set new doctype
            //xml.RemoveChild(n);
            //subset = string.Format("<!ELEMENT {1} ANY>{0}<!ATTLIST {1} id ID #REQUIRED>{0}{2}", Environment.NewLine, contentTypeAlias, subset);
            //var doctype = xml.CreateDocumentType("root", null, null, subset);
            //xml.InsertAfter(doctype, xml.FirstChild);

            var xml2 = new XmlDocument();
            subset = string.Format("<!ELEMENT {1} ANY>{0}<!ATTLIST {1} id ID #REQUIRED>{0}{2}", Environment.NewLine, contentTypeAlias, subset);
            var doctype = xml2.CreateDocumentType("root", null, null, subset);
            xml2.AppendChild(doctype);
            xml2.AppendChild(xml2.ImportNode(xml.DocumentElement, true));
            return xml2;
        }

        private static void InitializeXml(XmlDocument xml, string dtd)
        {
            // prime the xml document with an inline dtd and a root element
            xml.LoadXml(String.Format("<?xml version=\"1.0\" encoding=\"utf-8\" ?>{0}{1}{0}<root id=\"-1\"/>",
                Environment.NewLine, dtd));
        }

        // try to load from file, otherwise database
        // assumes xml lock (file is always locked)
        private void LoadXmlLocked(SafeXmlReaderWriter safeXml, out bool registerXmlChange)
        {
            LogHelper.Debug<content>("Loading Xml...");

            // try to get it from the file
            if (XmlFileEnabled && (safeXml.Xml = LoadXmlFromFile()) != null)
            {
                registerXmlChange = false; // loaded from disk, do NOT write back to disk!
                return;
            }

            // get it from the database, and register
            safeXml.Xml = LoadContentFromDatabase();
            registerXmlChange = true;
        }

        // NOTE
        // - this is NOT a reader/writer lock and each lock is exclusive
        // - these locks are NOT reentrant / recursive

        // gets a locked safe read access to the main xml
        private SafeXmlReaderWriter GetSafeXmlReader()
        {
            var releaser = _xmlLock.Lock();
            return SafeXmlReaderWriter.GetReader(this, releaser);
        }

        // gets a locked safe write access to the main xml (cloned)
        private SafeXmlReaderWriter GetSafeXmlWriter(bool auto = true)
        {
            var releaser = _xmlLock.Lock();
            return SafeXmlReaderWriter.GetWriter(this, releaser, auto);
        }

        private class SafeXmlReaderWriter : IDisposable
        {
            private readonly content _instance;
            private IDisposable _releaser;
            private bool _isWriter;
            private bool _auto;
            private bool _committed;
            private XmlDocument _xml;

            private SafeXmlReaderWriter(content instance, IDisposable releaser, bool isWriter, bool auto)
            {
                _instance = instance;
                _releaser = releaser;
                _isWriter = isWriter;
                _auto = auto;

                // cloning for writer is not an option anymore (see XmlIsImmutable)
                _xml = _isWriter ? Clone(instance._xmlContent) : instance._xmlContent;
            }

            public static SafeXmlReaderWriter GetReader(content instance, IDisposable releaser)
            {
                return new SafeXmlReaderWriter(instance, releaser, false, false);
            }

            public static SafeXmlReaderWriter GetWriter(content instance, IDisposable releaser, bool auto)
            {
                return new SafeXmlReaderWriter(instance, releaser, true, auto);
            }

            public void UpgradeToWriter(bool auto)
            {
                if (_isWriter)
                    throw new InvalidOperationException("Already writing.");
                _isWriter = true;
                _auto = auto;
                _xml = Clone(_xml); // cloning for writer is not an option anymore (see XmlIsImmutable)
            }

            public XmlDocument Xml
            {
                get
                {
                    return _xml;
                }
                set
                {
                    if (_isWriter == false)
                        throw new InvalidOperationException("Not writing.");
                    _xml = value;
                }
            }

            // registerXmlChange indicates whether to do what should be done when Xml changes,
            // that is, to request that the file be written to disk - something we don't want
            // to do if we're committing Xml precisely after we've read from disk!
            public void Commit(bool registerXmlChange = true)
            {
                if (_isWriter == false)
                    throw new InvalidOperationException("Not writing.");
                _instance.SetXmlLocked(Xml, registerXmlChange);
                _committed = true;
            }

            public void Dispose()
            {
                if (_releaser == null)
                    return;
                if (_isWriter && _auto && _committed == false)
                    Commit();
                _releaser.Dispose();
                _releaser = null;
            }
        }

        private static string ChildNodesXPath
        {
            get { return "./* [@id]"; }
        }

        private static string DataNodesXPath
        {
            get { return "./* [not(@id)]"; }
        }

        #endregion

        #region File

        private readonly string _xmlFileName = IOHelper.MapPath(SystemFiles.ContentCacheXml);
        private DateTime _lastFileRead; // last time the file was read
        private DateTime _nextFileCheck; // last time we checked whether the file was changed

        // not used - just try to read the file
        //private bool XmlFileExists
        //{
        //    get
        //    {
        //        // check that the file exists and has content (is not empty)
        //        var fileInfo = new FileInfo(_xmlFileName);
        //        return fileInfo.Exists && fileInfo.Length > 0;
        //    }
        //}

        private DateTime XmlFileLastWriteTime
        {
            get
            {
                var fileInfo = new FileInfo(_xmlFileName);
                return fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTime.MinValue;
            }
        }

        // invoked by XmlCacheFilePersister ONLY and that one manages the MainDom, ie it
        // will NOT try to save once the current app domain is not the main domain anymore
        // (no need to test _released)
        internal void SaveXmlToFile()
        {
            LogHelper.Info<content>("Save Xml to file...");

            try
            {
                var xml = _xmlContent; // capture (atomic + volatile), immutable anyway
                if (xml == null) return;

                // delete existing file, if any
                DeleteXmlFile();

                // ensure cache directory exists
                var directoryName = Path.GetDirectoryName(_xmlFileName);
                if (directoryName == null)
                    throw new Exception(string.Format("Invalid XmlFileName \"{0}\".", _xmlFileName));
                if (File.Exists(_xmlFileName) == false && Directory.Exists(directoryName) == false)
                    Directory.CreateDirectory(directoryName);

                // save
                using (var fs = new FileStream(_xmlFileName, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: true))
                {
                    var bytes = Encoding.UTF8.GetBytes(SaveXmlToString(xml));
                    fs.Write(bytes, 0, bytes.Length);
                }

                LogHelper.Info<content>("Saved Xml to file.");
            }
            catch (Exception e)
            {
                // if something goes wrong remove the file
                DeleteXmlFile();

                LogHelper.Error<content>("Failed to save Xml to file.", e);
            }
        }

        // invoked by XmlCacheFilePersister ONLY and that one manages the MainDom, ie it
        // will NOT try to save once the current app domain is not the main domain anymore
        // (no need to test _released)
        internal async Task SaveXmlToFileAsync()
        {
            LogHelper.Info<content>("Save Xml to file...");

            try
            {
                var xml = _xmlContent; // capture (atomic + volatile), immutable anyway
                if (xml == null) return;

                // delete existing file, if any
                DeleteXmlFile();

                // ensure cache directory exists
                var directoryName = Path.GetDirectoryName(_xmlFileName);
                if (directoryName == null)
                    throw new Exception(string.Format("Invalid XmlFileName \"{0}\".", _xmlFileName));
                if (File.Exists(_xmlFileName) == false && Directory.Exists(directoryName) == false)
                    Directory.CreateDirectory(directoryName);

                // save
                using (var fs = new FileStream(_xmlFileName, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: true))
                {
                    var bytes = Encoding.UTF8.GetBytes(SaveXmlToString(xml));
                    await fs.WriteAsync(bytes, 0, bytes.Length);
                }

                LogHelper.Info<content>("Saved Xml to file.");
            }
            catch (Exception e)
            {
                // if something goes wrong remove the file
                DeleteXmlFile();

                LogHelper.Error<content>("Failed to save Xml to file.", e);
            }
        }

        private string SaveXmlToString(XmlDocument xml)
        {
            // using that one method because we want to have proper indent
            // and in addition, writing async is never fully async because
            // althouth the writer is async, xml.WriteTo() will not async

            // that one almost works but... "The elements are indented as long as the element 
            // does not contain mixed content. Once the WriteString or WriteWhitespace method
            // is called to write out a mixed element content, the XmlWriter stops indenting. 
            // The indenting resumes once the mixed content element is closed." - says MSDN
            // about XmlWriterSettings.Indent

            // so ImportContent must also make sure of ignoring whitespaces!

            var sb = new StringBuilder();
            using (var xmlWriter = XmlWriter.Create(sb, new XmlWriterSettings
            {
                Indent = true,
                Encoding = Encoding.UTF8,
                //OmitXmlDeclaration = true
            }))
            {
                //xmlWriter.WriteProcessingInstruction("xml", "version=\"1.0\" encoding=\"utf-8\"");
                xml.WriteTo(xmlWriter); // already contains the xml declaration
            }
            return sb.ToString();
        }

        private XmlDocument LoadXmlFromFile()
        {
            // do NOT try to load if we are not the main domain anymore
            if (_released) return null;

            LogHelper.Info<content>("Load Xml from file...");

            try
            {
                var xml = new XmlDocument();
                using (var fs = new FileStream(_xmlFileName, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    xml.Load(fs);
                }
                _lastFileRead = DateTime.UtcNow;
                LogHelper.Info<content>("Loaded Xml from file.");
                return xml;
            }
            catch (FileNotFoundException)
            {
                LogHelper.Warn<content>("Failed to load Xml, file does not exist.");
                return null;
            }
            catch (Exception e)
            {
                LogHelper.Error<content>("Failed to load Xml from file.", e);
                DeleteXmlFile();
                return null;
            }
        }

        private void DeleteXmlFile()
        {
            if (File.Exists(_xmlFileName) == false) return;
            File.SetAttributes(_xmlFileName, FileAttributes.Normal);
            File.Delete(_xmlFileName);
        }

        private void ReloadXmlFromFileIfChanged()
        {
            if (SyncFromXmlFile == false) return;

            var now = DateTime.UtcNow;
            if (now < _nextFileCheck) return;

            // time to check
            _nextFileCheck = now.AddSeconds(1); // check every 1s
            if (XmlFileLastWriteTime <= _lastFileRead) return;

            LogHelper.Debug<content>("Xml file change detected, reloading.");

            // time to read

            using (var safeXml = GetSafeXmlWriter(false))
            {
                bool registerXmlChange;
                LoadXmlLocked(safeXml, out registerXmlChange); // updates _lastFileRead
                safeXml.Commit(registerXmlChange);
            }
        }

        #endregion

        #region Manage change

        //TODO remove as soon as we can break backward compatibility
        [Obsolete("Use GetAddOrUpdateXmlNode which returns an updated Xml document.", false)]
        public static void AddOrUpdateXmlNode(XmlDocument xml, int id, int level, int parentId, XmlNode docNode)
        {
            GetAddOrUpdateXmlNode(xml, id, level, parentId, docNode);
        }

        // adds or updates a node (docNode) into a cache (xml)
        public static XmlDocument GetAddOrUpdateXmlNode(XmlDocument xml, int id, int level, int parentId, XmlNode docNode)
        {
            // sanity checks
            if (id != docNode.AttributeValue<int>("id"))
                throw new ArgumentException("Values of id and docNode/@id are different.");
            if (parentId != docNode.AttributeValue<int>("parentID"))
                throw new ArgumentException("Values of parentId and docNode/@parentID are different.");

            // find the document in the cache
            XmlNode currentNode = xml.GetElementById(id.ToInvariantString());

            // if the document is not there already then it's a new document
            // we must make sure that its document type exists in the schema
            if (currentNode == null)
            {
                var xml2 = EnsureSchema(docNode.Name, xml);
                if (ReferenceEquals(xml, xml2) == false)
                    docNode = xml2.ImportNode(docNode, true);
                xml = xml2;
            }

            // find the parent
            XmlNode parentNode = level == 1
                ? xml.DocumentElement
                : xml.GetElementById(parentId.ToInvariantString());

            // no parent = cannot do anything
            if (parentNode == null)
                return xml;

            // insert/move the node under the parent
            if (currentNode == null)
            {
                // document not there, new node, append
                currentNode = docNode;
                parentNode.AppendChild(currentNode);
            }
            else
            {
                // document found... we could just copy the currentNode children nodes over under
                // docNode, then remove currentNode and insert docNode... the code below tries to
                // be clever and faster, though only benchmarking could tell whether it's worth the
                // pain...

                // first copy current parent ID - so we can compare with target parent
                var moving = currentNode.AttributeValue<int>("parentID") != parentId;

                if (docNode.Name == currentNode.Name)
                {
                    // name has not changed, safe to just update the current node
                    // by transfering values eg copying the attributes, and importing the data elements
                    TransferValuesFromDocumentXmlToPublishedXml(docNode, currentNode);

                    // if moving, move the node to the new parent
                    // else it's already under the right parent
                    // (but maybe the sort order has been updated)
                    if (moving)
                        parentNode.AppendChild(currentNode); // remove then append to parentNode
                }
                else
                {
                    // name has changed, must use docNode (with new name)
                    // move children nodes from currentNode to docNode (already has properties)
                    var children = currentNode.SelectNodes(ChildNodesXPath);
                    if (children == null) throw new Exception("oops");
                    foreach (XmlNode child in children)
                        docNode.AppendChild(child); // remove then append to docNode

                    // and put docNode in the right place - if parent has not changed, then
                    // just replace, else remove currentNode and insert docNode under the right parent
                    // (but maybe not at the right position due to sort order)
                    if (moving)
                    {
                        if (currentNode.ParentNode == null) throw new Exception("oops");
                        currentNode.ParentNode.RemoveChild(currentNode);
                        parentNode.AppendChild(docNode);
                    }
                    else
                    {
                        // replacing might screw the sort order
                        parentNode.ReplaceChild(docNode, currentNode);
                    }

                    currentNode = docNode;
                }
            }

            // if the nodes are not ordered, must sort
            // (see U4-509 + has to work with ReplaceChild too)
            //XmlHelper.SortNodesIfNeeded(parentNode, childNodesXPath, x => x.AttributeValue<int>("sortOrder"));

            // but...
            // if we assume that nodes are always correctly sorted
            // then we just need to ensure that currentNode is at the right position.
            // should be faster that moving all the nodes around.
            XmlHelper.SortNode(parentNode, ChildNodesXPath, currentNode, x => x.AttributeValue<int>("sortOrder"));
            return xml;
        }

        private static void TransferValuesFromDocumentXmlToPublishedXml(XmlNode documentNode, XmlNode publishedNode)
        {
            // remove all attributes from the published node
            if (publishedNode.Attributes == null) throw new Exception("oops");
            publishedNode.Attributes.RemoveAll();

            // remove all data nodes from the published node
            var dataNodes = publishedNode.SelectNodes(DataNodesXPath);
            if (dataNodes == null) throw new Exception("oops");
            foreach (XmlNode n in dataNodes)
                publishedNode.RemoveChild(n);

            // append all attributes from the document node to the published node
            if (documentNode.Attributes == null) throw new Exception("oops");
            foreach (XmlAttribute att in documentNode.Attributes)
                ((XmlElement)publishedNode).SetAttribute(att.Name, att.Value);

            // find the first child node, if any
            var childNodes = publishedNode.SelectNodes(ChildNodesXPath);
            if (childNodes == null) throw new Exception("oops");
            var firstChildNode = childNodes.Count == 0 ? null : childNodes[0];

            // append all data nodes from the document node to the published node
            dataNodes = documentNode.SelectNodes(DataNodesXPath);
            if (dataNodes == null) throw new Exception("oops");
            foreach (XmlNode n in dataNodes)
            {
                if (publishedNode.OwnerDocument == null) throw new Exception("oops");
                var imported = publishedNode.OwnerDocument.ImportNode(n, true);
                if (firstChildNode == null)
                    publishedNode.AppendChild(imported);
                else
                    publishedNode.InsertBefore(imported, firstChildNode);
            }
        }

        #endregion

        #region Events
        
        public delegate void DocumentCacheEventHandler(Document sender, DocumentCacheEventArgs e);

        public delegate void RefreshContentEventHandler(Document sender, RefreshContentEventArgs e);
      
        /// <summary>
        /// Occurs when [after document cache update].
        /// </summary>
        public static event DocumentCacheEventHandler AfterUpdateDocumentCache;

        /// <summary>
        /// Fires after document cache updater.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="DocumentCacheEventArgs"/> instance containing the event data.</param>
        protected virtual void FireAfterUpdateDocumentCache(Document sender, DocumentCacheEventArgs e)
        {
            if (AfterUpdateDocumentCache != null)
            {
                AfterUpdateDocumentCache(sender, e);
            }
        }

        public static event DocumentCacheEventHandler AfterClearDocumentCache;

        /// <summary>
        /// Fires the after document cache unpublish.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="DocumentCacheEventArgs"/> instance containing the event data.</param>
        protected virtual void FireAfterClearDocumentCache(Document sender, DocumentCacheEventArgs e)
        {
            if (AfterClearDocumentCache != null)
            {
                AfterClearDocumentCache(sender, e);
            }
        }


        public class DocumentCacheEventArgs : System.ComponentModel.CancelEventArgs { }
        public class RefreshContentEventArgs : System.ComponentModel.CancelEventArgs { }

        #endregion
    }
}