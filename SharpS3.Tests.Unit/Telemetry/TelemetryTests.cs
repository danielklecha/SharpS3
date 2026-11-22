namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Protocol;

[TestClass]
public class TelemetryTests
{
    [TestMethod]
    public void Settings_DefaultValues_And_CustomConfiguration()
    {
        var settings = new S3ServerTelemetrySettings();
        settings.MeterName.Should().Be(S3ServerTelemetryNames.DefaultSourceName);
        settings.ActivitySourceName.Should().Be(S3ServerTelemetryNames.DefaultSourceName);
        settings.IncludeBucketNames.Should().BeTrue();
        settings.IncludeObjectKeys.Should().BeFalse();
        settings.Enable.Should().BeTrue();
        settings.EnableMetrics.Should().BeTrue();
        settings.EnableTraces.Should().BeTrue();

        settings.IncludeBucketNames = false;
        settings.IncludeObjectKeys = true;
        settings.MeterName = "CustomMeter";
        settings.ActivitySourceName = "CustomSource";

        settings.IncludeBucketNames.Should().BeFalse();
        settings.IncludeObjectKeys.Should().BeTrue();
        settings.MeterName.Should().Be("CustomMeter");
        settings.ActivitySourceName.Should().Be("CustomSource");

        Action actEmptyMeter = () => settings.MeterName = "";
        actEmptyMeter.Should().Throw<ArgumentNullException>();

        Action actEmptySource = () => settings.ActivitySourceName = "";
        actEmptySource.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void S3ServerTelemetry_InactiveListeners_BeginRequestReturnsNull()
    {
        var serverSettings = new SharpS3Settings();
        using var telemetry = new S3ServerTelemetry(new S3ServerTelemetrySettings(), () => serverSettings, () => true);
        var req = telemetry.BeginRequest();
        req.Should().BeNull();
    }

    [TestMethod]
    public void S3ServerTelemetry_FullLifecycle_WithListeners()
    {
        var receivedTags = new List<KeyValuePair<string, object?>>();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == S3ServerTelemetryNames.DefaultSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(activityListener);

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (inst, listener) =>
        {
            if (inst.Meter.Name == S3ServerTelemetryNames.DefaultSourceName)
            {
                listener.EnableMeasurementEvents(inst);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((inst, measurement, tags, state) =>
        {
            foreach (var t in tags) receivedTags.Add(t);
        });
        meterListener.SetMeasurementEventCallback<double>((inst, measurement, tags, state) =>
        {
            foreach (var t in tags) receivedTags.Add(t);
        });
        meterListener.Start();

        var settings = new S3ServerTelemetrySettings
        {
            IncludeBucketNames = true,
            IncludeObjectKeys = true
        };
        var serverSettings = new SharpS3Settings();
        using var telemetry = new S3ServerTelemetry(settings, () => serverSettings, () => true);

        telemetry.Source.Should().NotBeNull();
        telemetry.Meter.Should().NotBeNull();

        // Direct recording methods
        telemetry.RecordRequest("GetObject", S3ServerTelemetryNames.OutcomeSuccess, S3ServerTelemetryNames.HandlerCallback, null!, 0.05);
        telemetry.RecordRequest("GetObject", S3ServerTelemetryNames.OutcomeError, S3ServerTelemetryNames.HandlerCallback, "NoSuchKey", 0.01);
        telemetry.RecordStage(S3ServerTelemetryNames.StageParse, S3ServerTelemetryNames.OutcomeSuccess, 0.002);
        telemetry.RecordCallback("StorageRead", S3ServerTelemetryNames.OutcomeSuccess, null!, 0.01);
        telemetry.RecordCallback("StorageRead", S3ServerTelemetryNames.OutcomeS3Error, "NoSuchKey", 0.01);
        telemetry.RecordCallback("StorageRead", S3ServerTelemetryNames.OutcomeException, "InvalidOperationException", 0.01);
        telemetry.AddActive(1);
        telemetry.AddActive(-1);
        telemetry.RecordSignature("v4", S3ServerTelemetryNames.OutcomeValid, 0.001);
        telemetry.RecordObjectSize("PutObject", 1024);
        telemetry.RecordError("NoSuchBucket", S3ServerTelemetryNames.StageParse);
        telemetry.RecordLifecycle("start");

        // ErrorTypeOf utility
        S3ServerTelemetry.ErrorTypeOf(new S3Exception(new Error(ErrorCode.NoSuchBucket))).Should().Be("NoSuchBucket");
        S3ServerTelemetry.ErrorTypeOf(new InvalidOperationException("boom")).Should().Be("System.InvalidOperationException");
        S3ServerTelemetry.ErrorTypeOf(null!).Should().BeNull();

        // Begin request
        var req = telemetry.BeginRequest();
        req.Should().NotBeNull();

        // Describe request with all tags
        var s3Req = new S3Request(
            "GET",
            "/my-bucket/folder/file.txt",
            new Dictionary<string, string> { { "uploadId", "upload-123" }, { "partNumber", "2" } },
            new Dictionary<string, string[]> { { "x-amz-request-id", new[] { "req-999" } } },
            Stream.Null);

        req!.Describe(s3Req);
        req.Describe(null!); // safe null handling

        // Test stages & callbacks
        using (var stage = req.StartStage(S3ServerTelemetryNames.StageSignatureValidation))
        {
            stage.Should().NotBeNull();
        }

        // Test fail filter
        using (var cbStage = req.StartCallback("CustomHandler"))
        {
            var testEx = new S3Exception(new Error(ErrorCode.InternalError));
            bool filterResult = TelemetryStage.Fail(cbStage, testEx);
            filterResult.Should().BeFalse(); // Exception filters must return false

            // Repeat fail should be ignored (first exception wins)
            TelemetryStage.Fail(cbStage, new Exception("secondary"));
        }

        // Test PostRequestHandler stage exception doesn't rewrite request outcome
        using (var postStage = req.StartStage(S3ServerTelemetryNames.StagePostRequestHandler))
        {
            TelemetryStage.Fail(postStage, new Exception("PostHandlerError"));
        }

        // Test serverFault >= 500 vs client fault < 500
        using (var clientErrStage = req.StartStage("ClientFaultStage"))
        {
            TelemetryStage.Fail(clientErrStage, new S3Exception(new Error(ErrorCode.NoSuchKey)));
        }

        using (var genericErrStage = req.StartStage("GenericErrStage"))
        {
            TelemetryStage.Fail(genericErrStage, new InvalidOperationException("fatal"));
        }

        // Complete request
        req.MarkSignatureOutcome(S3ServerTelemetryNames.OutcomeValid);
        req.Complete(200);

        // AsyncLocal scope
        RequestTelemetry.StartCurrentStage("DetachedStage").Should().BeNull();
        RequestTelemetry.Current = req;
        using (var currentStage = RequestTelemetry.StartCurrentStage("AttachedStage"))
        {
            currentStage.Should().NotBeNull();
        }
        RequestTelemetry.Current = null;
    }

    [TestMethod]
    public void RequestTelemetry_Describe_WithDisabledSettings()
    {
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == S3ServerTelemetryNames.DefaultSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(activityListener);

        var settings = new S3ServerTelemetrySettings
        {
            IncludeBucketNames = false,
            IncludeObjectKeys = false
        };
        var serverSettings = new SharpS3Settings();
        using var telemetry = new S3ServerTelemetry(settings, () => serverSettings, () => true);
        var req = telemetry.BeginRequest();
        req.Should().NotBeNull();

        var s3Req = new S3Request(
            "GET",
            "/bucket-hidden/key-hidden",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>(),
            Stream.Null);

        req!.Describe(s3Req);
        req.Complete(200);
    }

    [TestMethod]
    public void TelemetryStage_Fail_CatchBlock_Covered()
    {
        var stage = (TelemetryStage)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TelemetryStage));

        // Fail catch block when stage._Request is null (lines 80-82)
        bool result = TelemetryStage.Fail(stage, new Exception("test-fail-exception"));
        result.Should().BeFalse();

        // Dispose catch block when _Request is null (lines 146-148)
        Action actDispose = () => stage.Dispose();
        actDispose.Should().NotThrow();

        // AddExceptionEvent catch block and null checks (lines 89, 99-101)
        using var activity = new Activity("test-activity");
        activity.Start();
        TelemetryStage.AddExceptionEvent(activity, new ThrowingException());
        TelemetryStage.AddExceptionEvent(null!, null!);
        TelemetryStage.AddExceptionEvent(activity, null!);
    }

    [TestMethod]
    public void S3ServerTelemetry_RecordCallback_AllBranchesAndGuards()
    {
        var invocationMeasurements = new List<(long Value, Dictionary<string, object?> Tags)>();
        var durationMeasurements = new List<(double Value, Dictionary<string, object?> Tags)>();

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (inst, listener) =>
        {
            if (inst.Meter.Name == "CallbackTestMeter")
            {
                listener.EnableMeasurementEvents(inst);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((inst, measurement, tags, state) =>
        {
            if (inst.Name == S3ServerTelemetryNames.CallbackInvocations)
            {
                var dict = new Dictionary<string, object?>();
                foreach (var t in tags) dict[t.Key] = t.Value;
                invocationMeasurements.Add((measurement, dict));
            }
        });
        meterListener.SetMeasurementEventCallback<double>((inst, measurement, tags, state) =>
        {
            if (inst.Name == S3ServerTelemetryNames.CallbackDuration)
            {
                var dict = new Dictionary<string, object?>();
                foreach (var t in tags) dict[t.Key] = t.Value;
                durationMeasurements.Add((measurement, dict));
            }
        });
        meterListener.Start();

        var settings = new S3ServerTelemetrySettings { MeterName = "CallbackTestMeter" };
        var serverSettings = new SharpS3Settings();
        using var telemetry = new S3ServerTelemetry(settings, () => serverSettings, () => true);

        // 1. Success with null errorType (should NOT have error.type tag)
        telemetry.RecordCallback("Object.Read", S3ServerTelemetryNames.OutcomeSuccess, null, 0.05);

        invocationMeasurements.Should().HaveCount(1);
        invocationMeasurements[0].Value.Should().Be(1);
        invocationMeasurements[0].Tags[S3ServerTelemetryNames.AttributeCallback].Should().Be("Object.Read");
        invocationMeasurements[0].Tags[S3ServerTelemetryNames.AttributeOutcome].Should().Be(S3ServerTelemetryNames.OutcomeSuccess);
        invocationMeasurements[0].Tags.ContainsKey(S3ServerTelemetryNames.AttributeErrorType).Should().BeFalse();

        durationMeasurements.Should().HaveCount(1);
        durationMeasurements[0].Value.Should().Be(0.05);
        durationMeasurements[0].Tags[S3ServerTelemetryNames.AttributeCallback].Should().Be("Object.Read");
        durationMeasurements[0].Tags[S3ServerTelemetryNames.AttributeOutcome].Should().Be(S3ServerTelemetryNames.OutcomeSuccess);

        // 2. Empty errorType (should NOT have error.type tag)
        telemetry.RecordCallback("Object.Write", S3ServerTelemetryNames.OutcomeSuccess, "", 0.02);

        invocationMeasurements.Should().HaveCount(2);
        invocationMeasurements[1].Value.Should().Be(1);
        invocationMeasurements[1].Tags[S3ServerTelemetryNames.AttributeCallback].Should().Be("Object.Write");
        invocationMeasurements[1].Tags.ContainsKey(S3ServerTelemetryNames.AttributeErrorType).Should().BeFalse();

        // 3. Error with non-empty errorType (SHOULD have error.type tag)
        telemetry.RecordCallback("Object.Read", S3ServerTelemetryNames.OutcomeS3Error, "NoSuchKey", 0.01);

        invocationMeasurements.Should().HaveCount(3);
        invocationMeasurements[2].Value.Should().Be(1);
        invocationMeasurements[2].Tags[S3ServerTelemetryNames.AttributeCallback].Should().Be("Object.Read");
        invocationMeasurements[2].Tags[S3ServerTelemetryNames.AttributeOutcome].Should().Be(S3ServerTelemetryNames.OutcomeS3Error);
        invocationMeasurements[2].Tags[S3ServerTelemetryNames.AttributeErrorType].Should().Be("NoSuchKey");

        durationMeasurements.Should().HaveCount(3);
        durationMeasurements[2].Value.Should().Be(0.01);

        // 4. Null callback, null outcome, negative seconds (safely defaulted & clamped)
        telemetry.RecordCallback(null!, null!, "SomeError", -1.5);

        invocationMeasurements.Should().HaveCount(4);
        invocationMeasurements[3].Tags[S3ServerTelemetryNames.AttributeCallback].Should().Be("unknown");
        invocationMeasurements[3].Tags[S3ServerTelemetryNames.AttributeOutcome].Should().Be(S3ServerTelemetryNames.OutcomeSuccess);
        invocationMeasurements[3].Tags[S3ServerTelemetryNames.AttributeErrorType].Should().Be("SomeError");

        durationMeasurements.Should().HaveCount(4);
        durationMeasurements[3].Value.Should().Be(0); // clamped to 0
        durationMeasurements[3].Tags[S3ServerTelemetryNames.AttributeCallback].Should().Be("unknown");

        // 5. Disabled metrics (_Meter == null) guard check
        var disabledSettings = new S3ServerTelemetrySettings { EnableMetrics = false };
        using var disabledTelemetry = new S3ServerTelemetry(disabledSettings, () => serverSettings, () => true);
        disabledTelemetry.RecordCallback("Object.Read", S3ServerTelemetryNames.OutcomeSuccess, null, 0.05);

        // No new measurements recorded
        invocationMeasurements.Should().HaveCount(4);
        durationMeasurements.Should().HaveCount(4);
    }

    [TestMethod]
    public void RequestTelemetry_CallbackStage_RecordsExpectedCallbackMetrics()
    {
        var recordedCallbacks = new List<(string Callback, string Outcome, string? ErrorType)>();

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (inst, listener) =>
        {
            if (inst.Meter.Name == "CallbackStageMeter")
            {
                listener.EnableMeasurementEvents(inst);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((inst, measurement, tags, state) =>
        {
            if (inst.Name == S3ServerTelemetryNames.CallbackInvocations)
            {
                string? cb = null, outcome = null, err = null;
                foreach (var t in tags)
                {
                    if (t.Key == S3ServerTelemetryNames.AttributeCallback) cb = t.Value?.ToString();
                    if (t.Key == S3ServerTelemetryNames.AttributeOutcome) outcome = t.Value?.ToString();
                    if (t.Key == S3ServerTelemetryNames.AttributeErrorType) err = t.Value?.ToString();
                }
                recordedCallbacks.Add((cb!, outcome!, err));
            }
        });
        meterListener.Start();

        var settings = new S3ServerTelemetrySettings { MeterName = "CallbackStageMeter" };
        var serverSettings = new SharpS3Settings();
        using var telemetry = new S3ServerTelemetry(settings, () => serverSettings, () => true);
        var req = telemetry.BeginRequest()!;

        // 1. Success callback stage
        using (var stage = req.StartCallback("StorageRead"))
        {
        }

        // 2. S3Error callback stage
        using (var stage = req.StartCallback("StorageRead"))
        {
            TelemetryStage.Fail(stage, new S3Exception(new Error(ErrorCode.NoSuchKey)));
        }

        // 3. Exception callback stage
        using (var stage = req.StartCallback("StorageWrite"))
        {
            TelemetryStage.Fail(stage, new IOException("Disk error"));
        }

        recordedCallbacks.Should().HaveCount(3);
        recordedCallbacks[0].Should().Be(("StorageRead", S3ServerTelemetryNames.OutcomeSuccess, null));
        recordedCallbacks[1].Should().Be(("StorageRead", S3ServerTelemetryNames.OutcomeS3Error, "NoSuchKey"));
        recordedCallbacks[2].Should().Be(("StorageWrite", S3ServerTelemetryNames.OutcomeException, "System.IO.IOException"));
    }

    private class ThrowingException : Exception
    {
        public override string Message => throw new InvalidOperationException("simulated-error");
    }
}
