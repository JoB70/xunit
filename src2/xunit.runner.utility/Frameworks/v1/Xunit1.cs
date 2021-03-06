﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Xml;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Xunit
{
    /// <summary>
    /// This class be used to do discovery and execution of xUnit.net v1 tests
    /// using a reflection-based implementation of <see cref="IAssemblyInfo"/>.
    /// Runner authors are strongly encouraged to use <see cref="XunitFrontController"/>
    /// instead of using this class directly.
    /// </summary>
    public class Xunit1 : IFrontController
    {
        static readonly BinaryFormatter BinaryFormatter = new BinaryFormatter();

        readonly string assemblyFileName;
        readonly string configFileName;
        readonly IXunit1Executor executor;
        readonly ISourceInformationProvider sourceInformationProvider;
        readonly ITestCollection testCollection;

        /// <summary>
        /// Initializes a new instance of the <see cref="Xunit1"/> class.
        /// </summary>
        /// <param name="sourceInformationProvider">Source code information provider.</param>
        /// <param name="assemblyFileName">The test assembly.</param>
        /// <param name="configFileName">The test assembly configuration file.</param>
        /// <param name="shadowCopy">If set to <c>true</c>, runs tests in a shadow copied app domain, which allows
        /// tests to be discovered and run without locking assembly files on disk.</param>
        public Xunit1(ISourceInformationProvider sourceInformationProvider, string assemblyFileName, string configFileName = null, bool shadowCopy = true)
        {
            this.sourceInformationProvider = sourceInformationProvider;
            this.assemblyFileName = assemblyFileName;
            this.configFileName = configFileName;

            executor = CreateExecutor(assemblyFileName, configFileName, shadowCopy);
            testCollection = new Xunit1TestCollection(assemblyFileName);
        }

        /// <inheritdoc/>
        public string TestFrameworkDisplayName
        {
            get { return executor.TestFrameworkDisplayName; }
        }

        /// <summary>
        /// Creates a wrapper to call the Executor call from xUnit.net v1.
        /// </summary>
        /// <param name="testAssemblyFileName">The filename of the assembly under test.</param>
        /// <param name="configFileName">The configuration file to be used for the app domain (optional, may be <c>null</c>).</param>
        /// <param name="shadowCopy">Whether to enable shadow copy for the app domain.</param>
        /// <returns>The executor wrapper.</returns>
        protected virtual IXunit1Executor CreateExecutor(string testAssemblyFileName, string configFileName, bool shadowCopy)
        {
            return new Xunit1Executor(testAssemblyFileName, configFileName, shadowCopy);
        }

        /// <inheritdoc/>
        public ITestCase Deserialize(string value)
        {
            using (var stream = new MemoryStream(Convert.FromBase64String(value)))
            {
                var result = (Xunit1TestCase)BinaryFormatter.Deserialize(stream);
                result.TestCollection = testCollection;
                return result;
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            executor.SafeDispose();
        }

        /// <inheritdoc/>
        public void Find(bool includeSourceInformation, IMessageSink messageSink)
        {
            Find(msg => true, includeSourceInformation, messageSink);
        }

        /// <inheritdoc/>
        public void Find(string typeName, bool includeSourceInformation, IMessageSink messageSink)
        {
            Find(msg => msg.TestCase.Class.Name == typeName, includeSourceInformation, messageSink);
        }

        void Find(Predicate<ITestCaseDiscoveryMessage> filter, bool includeSourceInformation, IMessageSink messageSink)
        {
            try
            {
                XmlNode assemblyXml = null;
                var handler = new XmlNodeCallbackHandler(xml => { assemblyXml = xml; return true; });
                executor.EnumerateTests(handler);

                foreach (XmlNode method in assemblyXml.SelectNodes("//method"))
                {
                    var testCase = method.ToTestCase(assemblyFileName);
                    if (testCase != null)
                    {
                        if (includeSourceInformation)
                            testCase.SourceInformation = sourceInformationProvider.GetSourceInformation(testCase);

                        testCase.TestCollection = testCollection;

                        var message = new TestCaseDiscoveryMessage(testCase);
                        if (filter(message))
                            messageSink.OnMessage(message);
                    }
                }
            }
            finally
            {
                messageSink.OnMessage(new DiscoveryCompleteMessage(new string[0]));
            }
        }

        /// <inheritdoc/>
        public void Run(IEnumerable<ITestCase> testCases, IMessageSink messageSink)
        {
            var results = new RunSummary();
            var environment = String.Format("{0}-bit .NET {1}", IntPtr.Size * 8, Environment.Version);

            // TODO: Contract for Run() states that null "testCases" means "run everything".

            if (messageSink.OnMessage(new TestAssemblyStarting(assemblyFileName, configFileName, DateTime.Now, environment, TestFrameworkDisplayName)))
                foreach (var testCollectionGroup in testCases.Cast<Xunit1TestCase>().GroupBy(tc => tc.TestCollection))
                {
                    var collectionResults = RunTestCollection(testCollectionGroup.Key, testCollectionGroup, messageSink);
                    results.Aggregate(collectionResults);
                    if (!collectionResults.Continue)
                        break;
                }

            messageSink.OnMessage(new TestAssemblyFinished(new Xunit1AssemblyInfo(assemblyFileName), results.Time, results.Total, results.Failed, results.Skipped));
        }

        RunSummary RunTestCollection(ITestCollection testCollection, IEnumerable<Xunit1TestCase> testCases, IMessageSink messageSink)
        {
            var results = new RunSummary();
            results.Continue = messageSink.OnMessage(new TestCollectionStarting(testCollection));

            if (results.Continue)
                foreach (var testClassGroup in testCases.GroupBy(tc => tc.Class.Name))
                {
                    var classResults = RunTestClass(testCollection, testClassGroup.Key, testClassGroup.ToList(), messageSink);
                    results.Aggregate(classResults);
                    if (!classResults.Continue)
                        break;
                }

            results.Continue = messageSink.OnMessage(new TestCollectionFinished(testCollection, results.Time, results.Total, results.Failed, results.Skipped)) && results.Continue;
            return results;
        }

        RunSummary RunTestClass(ITestCollection testCollection, string className, IList<Xunit1TestCase> testCases, IMessageSink messageSink)
        {
            var handler = new TestClassCallbackHandler(testCases, messageSink);
            var results = handler.TestClassResults;
            results.Continue = messageSink.OnMessage(new TestClassStarting(testCollection, className));

            if (results.Continue)
            {
                try
                {
                    var methodNames = testCases.Select(tc => tc.Method.Name).ToList();
                    executor.RunTests(className, methodNames, handler);
                    handler.LastNodeArrived.WaitOne();
                }
                catch (Exception ex)
                {
                    var stackTrace = ex.StackTrace;
                    var rethrowIndex = stackTrace.IndexOf("$$RethrowMarker$$");
                    if (rethrowIndex > -1)
                        stackTrace = stackTrace.Substring(0, rethrowIndex);

                    results.Continue = messageSink.OnMessage(new ErrorMessage(ex.GetType().FullName, ex.Message, stackTrace)) && results.Continue;
                }
            }

            results.Continue = messageSink.OnMessage(new TestClassFinished(testCollection, className, results.Time, results.Total, results.Failed, results.Skipped)) && results.Continue;
            return results;
        }

        /// <inheritdoc/>
        public string Serialize(ITestCase testCase)
        {
            using (var stream = new MemoryStream())
            {
                BinaryFormatter.Serialize(stream, testCase);
                return Convert.ToBase64String(stream.GetBuffer());
            }
        }
    }
}