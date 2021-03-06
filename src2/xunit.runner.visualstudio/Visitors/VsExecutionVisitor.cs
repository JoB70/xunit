﻿using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using Xunit.Abstractions;
using VsTestResult = Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResult;

namespace Xunit.Runner.VisualStudio
{
    public class VsExecutionVisitor : TestMessageVisitor<ITestAssemblyFinished>
    {
        readonly Func<bool> cancelledThunk;
        readonly ITestExecutionRecorder recorder;
        readonly Dictionary<ITestCase, TestCase> testCases;

        public VsExecutionVisitor(string source, ITestExecutionRecorder recorder, Dictionary<ITestCase, TestCase> testCases, Func<bool> cancelledThunk)
        {
            this.recorder = recorder;
            this.testCases = testCases;
            this.cancelledThunk = cancelledThunk;
        }

        protected override bool Visit(IErrorMessage error)
        {
            recorder.SendMessage(TestMessageLevel.Error, String.Format("Catastrophic failure: {0}", error.Message));

            return !cancelledThunk();
        }

        protected override bool Visit(ITestFailed testFailed)
        {
            VsTestResult result = MakeVsTestResult(testFailed, TestOutcome.Failed);
            result.ErrorMessage = testFailed.Message;
            result.ErrorStackTrace = testFailed.StackTrace;

            recorder.RecordEnd(result.TestCase, result.Outcome);
            recorder.RecordResult(result);

            return !cancelledThunk();
        }

        protected override bool Visit(ITestPassed testPassed)
        {
            VsTestResult result = MakeVsTestResult(testPassed, TestOutcome.Passed);
            recorder.RecordEnd(result.TestCase, result.Outcome);
            recorder.RecordResult(result);

            return !cancelledThunk();
        }

        protected override bool Visit(ITestSkipped testSkipped)
        {
            VsTestResult result = MakeVsTestResult(testSkipped, TestOutcome.Skipped);
            recorder.RecordEnd(result.TestCase, result.Outcome);
            recorder.RecordResult(result);

            return !cancelledThunk();
        }

        protected override bool Visit(ITestStarting testStarting)
        {
            recorder.RecordStart(testCases[testStarting.TestCase]);

            return !cancelledThunk();
        }

        private static string GetFullyQualifiedName(string type, string method)
        {
            return String.Format("{0}.{1}", type, method);
        }

        private VsTestResult MakeVsTestResult(ITestResultMessage testResult, TestOutcome outcome)
        {
            TestCase testCase = testCases[testResult.TestCase];

            VsTestResult result = new VsTestResult(testCase)
            {
                ComputerName = Environment.MachineName,
                DisplayName = testResult.TestDisplayName,
                Duration = TimeSpan.FromSeconds((double)testResult.ExecutionTime),
                Outcome = outcome,
            };

            // Work around VS considering a test "not run" when the duration is 0
            if (result.Duration.TotalMilliseconds == 0)
                result.Duration = TimeSpan.FromMilliseconds(1);

            return result;
        }
    }
}