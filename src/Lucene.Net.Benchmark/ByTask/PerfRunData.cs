﻿using Lucene.Net.Analysis;
using Lucene.Net.Benchmarks.ByTask.Feeds;
using Lucene.Net.Benchmarks.ByTask.Stats;
using Lucene.Net.Benchmarks.ByTask.Tasks;
using Lucene.Net.Benchmarks.ByTask.Utils;
using Lucene.Net.Facet.Taxonomy;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Console = Lucene.Net.Util.SystemConsole;

namespace Lucene.Net.Benchmarks.ByTask
{
    /*
     * Licensed to the Apache Software Foundation (ASF) under one or more
     * contributor license agreements.  See the NOTICE file distributed with
     * this work for additional information regarding copyright ownership.
     * The ASF licenses this file to You under the Apache License, Version 2.0
     * (the "License"); you may not use this file except in compliance with
     * the License.  You may obtain a copy of the License at
     *
     *     http://www.apache.org/licenses/LICENSE-2.0
     *
     * Unless required by applicable law or agreed to in writing, software
     * distributed under the License is distributed on an "AS IS" BASIS,
     * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
     * See the License for the specific language governing permissions and
     * limitations under the License.
     */

    /// <summary>
    /// Data maintained by a performance test run.
    /// </summary>
    /// <remarks>
    /// Data includes:
    /// <list type="bullet">
    ///     <item><description>Configuration.</description></item>
    ///     <item><description>Directory, Writer, Reader.</description></item>
    ///     <item><description>Taxonomy Directory, Writer, Reader.</description></item>
    ///     <item><description>DocMaker, FacetSource and a few instances of QueryMaker.</description></item>
    ///     <item><description>Named AnalysisFactories.</description></item>
    ///     <item><description>Analyzer.</description></item>
    ///     <item><description>Statistics data which updated during the run.</description></item>
    /// </list>
    /// <para/>
    /// Config properties:
    /// <list type="bullet">
    ///     <item><term>work.dir</term><description>&lt;path to root of docs and index dirs| Default: work&gt;</description></item>
    ///     <item><term>analyzer</term><description>&lt;class name for analyzer| Default: StandardAnalyzer&gt;</description></item>
    ///     <item><term>doc.maker</term><description>&lt;class name for doc-maker| Default: DocMaker&gt;</description></item>
    ///     <item><term>facet.source</term><description>&lt;class name for facet-source| Default: RandomFacetSource&gt;</description></item>
    ///     <item><term>query.maker</term><description>&lt;class name for query-maker| Default: SimpleQueryMaker&gt;</description></item>
    ///     <item><term>log.queries</term><description>&lt;whether queries should be printed| Default: false&gt;</description></item>
    ///     <item><term>directory</term><description>&lt;type of directory to use for the index| Default: RAMDirectory&gt;</description></item>
    ///     <item><term>taxonomy.directory</term><description>&lt;type of directory for taxonomy index| Default: RAMDirectory&gt;</description></item>
    /// </list>
    /// </remarks>
    public class PerfRunData : IDisposable
    {
        private Points points;

        // objects used during performance test run
        // directory, analyzer, docMaker - created at startup.
        // reader, writer, searcher - maintained by basic tasks. 
        private Store.Directory directory;
        private IDictionary<string, AnalyzerFactory> analyzerFactories = new Dictionary<string, AnalyzerFactory>();
        private Analyzer analyzer;
        private DocMaker docMaker;
        private ContentSource contentSource;
        private FacetSource facetSource;
        private CultureInfo locale;

        private Store.Directory taxonomyDir;
        private ITaxonomyWriter taxonomyWriter;
        private TaxonomyReader taxonomyReader;

        // we use separate (identical) instances for each "read" task type, so each can iterate the quries separately.
        private IDictionary<Type, IQueryMaker> readTaskQueryMaker;
        private Type qmkrClass;

        private DirectoryReader indexReader;
        private IndexSearcher indexSearcher;
        private IndexWriter indexWriter;
        private Config config;
        private long startTimeMillis;

        private readonly IDictionary<string, object> perfObjects = new Dictionary<string, object>();

        // constructor
        public PerfRunData(Config config)
        {
            this.config = config;
            // analyzer (default is standard analyzer)
            analyzer = NewAnalyzerTask.CreateAnalyzer(config.Get("analyzer",
                typeof(Lucene.Net.Analysis.Standard.StandardAnalyzer).AssemblyQualifiedName));

            // content source
            string sourceClass = config.Get("content.source", typeof(SingleDocSource).AssemblyQualifiedName);
            contentSource = (ContentSource)Activator.CreateInstance(Type.GetType(sourceClass)); //Class.forName(sourceClass).asSubclass(typeof(ContentSource)).newInstance();
            contentSource.SetConfig(config);

            // doc maker
            docMaker = (DocMaker)Activator.CreateInstance(Type.GetType(config.Get("doc.maker", typeof(DocMaker).AssemblyQualifiedName)));  // "org.apache.lucene.benchmark.byTask.feeds.DocMaker")).asSubclass(DocMaker.class).newInstance();
            docMaker.SetConfig(config, contentSource);
            // facet source
            facetSource = (FacetSource)Activator.CreateInstance(Type.GetType(config.Get("facet.source",
                typeof(RandomFacetSource).AssemblyQualifiedName))); // "org.apache.lucene.benchmark.byTask.feeds.RandomFacetSource")).asSubclass(FacetSource.class).newInstance();
            facetSource.SetConfig(config);
            // query makers
            readTaskQueryMaker = new Dictionary<Type, IQueryMaker>();
            qmkrClass = Type.GetType(config.Get("query.maker", typeof(SimpleQueryMaker).AssemblyQualifiedName));

            // index stuff
            Reinit(false);

            // statistic points
            points = new Points(config);

            if (bool.Parse(config.Get("log.queries", "false")))
            {
                Console.WriteLine("------------> queries:");
                Console.WriteLine(GetQueryMaker(new SearchTask(this)).PrintQueries());
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                IOUtils.Dispose(indexWriter, indexReader, directory,
                          taxonomyWriter, taxonomyReader, taxonomyDir,
                          docMaker, facetSource, contentSource);

                // close all perf objects that are closeable.
                List<IDisposable> perfObjectsToClose = new List<IDisposable>();
                foreach (object obj in perfObjects.Values)
                {
                    if (obj is IDisposable)
                    {
                        perfObjectsToClose.Add((IDisposable)obj);
                    }
                }
                IOUtils.Dispose(perfObjectsToClose);
            }
        }

        // clean old stuff, reopen 
        public virtual void Reinit(bool eraseIndex)
        {
            // cleanup index
            IOUtils.Dispose(indexWriter, indexReader, directory);
            indexWriter = null;
            indexReader = null;

            IOUtils.Dispose(taxonomyWriter, taxonomyReader, taxonomyDir);
            taxonomyWriter = null;
            taxonomyReader = null;

            // directory (default is ram-dir).
            directory = CreateDirectory(eraseIndex, "index", "directory");
            taxonomyDir = CreateDirectory(eraseIndex, "taxo", "taxonomy.directory");

            // inputs
            ResetInputs();

            // release unused stuff
            GC.Collect();

            // Re-init clock
            SetStartTimeMillis();
        }

        private Store.Directory CreateDirectory(bool eraseIndex, string dirName,
            string dirParam)
        {
            if ("FSDirectory".Equals(config.Get(dirParam, "RAMDirectory"), StringComparison.Ordinal))
            {
                DirectoryInfo workDir = new DirectoryInfo(config.Get("work.dir", "work"));
                DirectoryInfo indexDir = new DirectoryInfo(System.IO.Path.Combine(workDir.FullName, dirName));
                if (eraseIndex && indexDir.Exists)
                {
                    FileUtils.FullyDelete(indexDir);
                }
                indexDir.Create();
                return FSDirectory.Open(indexDir);
            }

            return new RAMDirectory();
        }

        /// <summary>
        /// Returns an object that was previously set by <see cref="SetPerfObject(string, object)"/>.
        /// </summary>
        public virtual object GetPerfObject(string key)
        {
            lock (this)
            {
                object result;
                perfObjects.TryGetValue(key, out result);
                return result;
            }
        }

        /// <summary>
        /// Sets an object that is required by <see cref="PerfTask"/>s, keyed by the given
        /// <paramref name="key"/>. If the object implements <see cref="IDisposable"/>, it will be disposed
        /// by <see cref="Dispose()"/>.
        /// </summary>
        public virtual void SetPerfObject(string key, object obj)
        {
            lock (this)
            {
                perfObjects[key] = obj;
            }
        }

        public virtual long SetStartTimeMillis()
        {
            startTimeMillis = J2N.Time.CurrentTimeMilliseconds();
            return startTimeMillis;
        }

        /// <summary>
        /// Gets start time in milliseconds.
        /// </summary>
        public virtual long StartTimeMillis
        {
            get { return startTimeMillis; }
        }

        /// <summary>
        /// Gets the points.
        /// </summary>
        public virtual Points Points
        {
            get { return points; }
        }

        /// <summary>
        /// Gets or sets the directory.
        /// </summary>
        public virtual Store.Directory Directory
        {
            get { return directory; }
            set { directory = value; }
        }

        /// <summary>
        /// Gets the taxonomy directory.
        /// </summary>
        public virtual Store.Directory TaxonomyDir
        {
            get { return taxonomyDir; }
        }

        /// <summary>
        /// Set the taxonomy reader. Takes ownership of that taxonomy reader, that is,
        /// internally performs taxoReader.IncRef() (If caller no longer needs that 
        /// reader it should DecRef()/Dispose() it after calling this method, otherwise, 
        /// the reader will remain open). 
        /// </summary>
        /// <param name="taxoReader">The taxonomy reader to set.</param>
        public virtual void SetTaxonomyReader(TaxonomyReader taxoReader)
        {
            lock (this)
            {
                if (taxoReader == this.taxonomyReader)
                {
                    return;
                }
                if (taxonomyReader != null)
                {
                    taxonomyReader.DecRef();
                }

                if (taxoReader != null)
                {
                    taxoReader.IncRef();
                }
                this.taxonomyReader = taxoReader;
            }
        }

        /// <summary>
        /// Returns the taxonomyReader.  NOTE: this returns a
        /// reference.  You must call TaxonomyReader.DecRef() when
        /// you're done.
        /// </summary>
        public virtual TaxonomyReader GetTaxonomyReader()
        {
            lock (this)
            {
                if (taxonomyReader != null)
                {
                    taxonomyReader.IncRef();
                }
                return taxonomyReader;
            }
        }

        /// <summary>
        /// Gets or sets the taxonomy writer.
        /// </summary>
        public virtual ITaxonomyWriter TaxonomyWriter
        {
            get { return taxonomyWriter; }
            set { taxonomyWriter = value; }
        }

        /// <summary>
        /// Returns the indexReader.  NOTE: this returns a
        /// reference.  You must call IndexReader.DecRef() when
        /// you're done.
        /// </summary>
        public virtual DirectoryReader GetIndexReader()
        {
            lock (this)
            {
                if (indexReader != null)
                {
                    indexReader.IncRef();
                }
                return indexReader;
            }
        }

        /// <summary>
        /// Returns the indexSearcher.  NOTE: this returns
        /// a reference to the underlying IndexReader.  You must
        /// call IndexReader.DecRef() when you're done.
        /// </summary>
        /// <returns></returns>
        public virtual IndexSearcher GetIndexSearcher()
        {
            lock (this)
            {
                if (indexReader != null)
                {
                    indexReader.IncRef();
                }
                return indexSearcher;
            }
        }

        /// <summary>
        /// Set the index reader. Takes ownership of that index reader, that is,
        /// internally performs indexReader.incRef() (If caller no longer needs that 
        /// reader it should decRef()/close() it after calling this method, otherwise,
        /// the reader will remain open). 
        /// </summary>
        /// <param name="indexReader">The indexReader to set.</param>
        public virtual void SetIndexReader(DirectoryReader indexReader)
        {
            lock (this)
            {
                if (indexReader == this.indexReader)
                {
                    return;
                }

                if (this.indexReader != null)
                {
                    // Release current IR
                    this.indexReader.DecRef();
                }

                this.indexReader = indexReader;
                if (indexReader != null)
                {
                    // Hold reference to new IR
                    indexReader.IncRef();
                    indexSearcher = new IndexSearcher(indexReader);
                }
                else
                {
                    indexSearcher = null;
                }
            }
        }

        /// <summary>
        /// Gets or sets the indexWriter.
        /// </summary>
        public virtual IndexWriter IndexWriter
        {
            get { return indexWriter; }
            set { indexWriter = value; }
        }

        /// <summary>
        /// Gets or sets the analyzer.
        /// </summary>
        public virtual Analyzer Analyzer
        {
            get { return analyzer; }
            set { analyzer = value; }
        }

        /// <summary>Gets the <see cref="Feeds.ContentSource"/>.</summary>
        public virtual ContentSource ContentSource
        {
            get { return contentSource; }
        }

        /// <summary>Returns the <see cref="Feeds.DocMaker"/>.</summary>
        public virtual DocMaker DocMaker
        {
            get { return docMaker; }
        }

        /// <summary>Gets the <see cref="Feeds.FacetSource"/>.</summary>
        public virtual FacetSource FacetSource
        {
            get { return facetSource; }
        }

        /// <summary>
        /// Gets or sets the culture.
        /// </summary>
        public virtual CultureInfo Locale
        {
            get { return locale; }
            set { locale = value; }
        }

        /// <summary>
        /// Gets the config.
        /// </summary>
        public virtual Config Config
        {
            get { return config; }
        }

        public virtual void ResetInputs()
        {
            contentSource.ResetInputs();
            docMaker.ResetInputs();
            facetSource.ResetInputs();
            foreach (IQueryMaker queryMaker in readTaskQueryMaker.Values)
            {
                queryMaker.ResetInputs();
            }
        }

        /// <summary>
        /// Returns the queryMaker by read task type (class).
        /// </summary>
        public virtual IQueryMaker GetQueryMaker(ReadTask readTask)
        {
            lock (this)
            {
                // mapping the query maker by task class allows extending/adding new search/read tasks
                // without needing to modify this class.
                Type readTaskClass = readTask.GetType();
                IQueryMaker qm;
                if (!readTaskQueryMaker.TryGetValue(readTaskClass, out qm) || qm == null)
                {
                    try
                    {
                        //qm = qmkrClass.newInstance();
                        qm = (IQueryMaker)Activator.CreateInstance(qmkrClass);
                        qm.SetConfig(config);
                    }
                    catch (Exception e)
                    {
                        throw new Exception(e.ToString(), e);
                    }
                    readTaskQueryMaker[readTaskClass] = qm;
                }
                return qm;
            }
        }

        public virtual IDictionary<string, AnalyzerFactory> AnalyzerFactories
        {
            get { return analyzerFactories; }
        }
    }
}
