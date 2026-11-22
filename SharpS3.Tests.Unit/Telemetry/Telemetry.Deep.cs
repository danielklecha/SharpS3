namespace SharpS3.Tests.Unit;

#pragma warning disable CS8625

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Reflection;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Protocol;

[TestClass]
public class TelemetryDeepTests
{
    [TestMethod]
    public void S3ServerTelemetry_ObservableGauges_And_Callbacks()
    {
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == "S3Server" || instr.Meter.Name == "SharpS3")
            {
                l.EnableMeasurementEvents(instr);
            }
        };
        listener.SetMeasurementEventCallback<int>((instr, measurement, tags, state) => { });
        listener.SetMeasurementEventCallback<long>((instr, measurement, tags, state) => { });
        listener.Start();

        var settings = new SharpS3Settings();
        using var telemetry = new S3ServerTelemetry(new S3ServerTelemetrySettings(), () => settings, () => true);

        // Trigger recording of all observable instruments
        listener.RecordObservableInstruments();

        // Test with settings.OperationLimits == null
        settings.OperationLimits = null!;
        listener.RecordObservableInstruments();

        // Test with throwing settings callback
        using var telemetryThrows = new S3ServerTelemetry(
            new S3ServerTelemetrySettings(),
            () => throw new InvalidOperationException("boom"),
            () => throw new InvalidOperationException("boom")
        );
        listener.RecordObservableInstruments();

        // Test with null callbacks
        using var telemetryNull = new S3ServerTelemetry(
            new S3ServerTelemetrySettings(),
            null!,
            null!
        );
        listener.RecordObservableInstruments();

        _ = S3ServerTelemetry.LibraryVersion;
        _ = telemetry.Settings;
        _ = telemetry.Source;
        _ = telemetry.Meter;
        telemetry.RecordObjectSize("Put", -1);
    }

    [TestMethod]
    public void S3ServerTelemetry_ThrowingListeners_Hit_Catch_Blocks()
    {
        var settings = new SharpS3Settings();
        using var telemetry = new S3ServerTelemetry(new S3ServerTelemetrySettings(), () => settings, () => true);

        using var throwingListener = new MeterListener();
        throwingListener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == "S3Server" || instr.Meter.Name == "SharpS3")
            {
                l.EnableMeasurementEvents(instr);
            }
        };
        throwingListener.SetMeasurementEventCallback<double>((_, _, _, _) => throw new InvalidOperationException("listener boom"));
        throwingListener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("listener boom"));
        throwingListener.SetMeasurementEventCallback<int>((_, _, _, _) => throw new InvalidOperationException("listener boom"));
        throwingListener.Start();

        telemetry.RecordRequest("GetObject", "Success", "handler", "NoSuchKey", 0.1);
        telemetry.AddActive(1);
        telemetry.RecordStage("parse", "Success", 0.01);
        telemetry.RecordCallback("GetBucket", "Success", "NoSuchKey", 0.02);
        telemetry.RecordCallback("GetBucket", "Success", null!, 0.02);
        telemetry.RecordCallback(null!, null!, "", -0.5);
        telemetry.RecordSignature("V4", "Success", 0.005);
        telemetry.RecordError("NoSuchKey", "internal");
        telemetry.RecordObjectSize("PutObject", 1024);
        telemetry.RecordLifecycle("Started");
    }

    [TestMethod]
    public void RequestTelemetry_Describe_MarkError_And_Complete_Paths()
    {
        using var actListener = new ActivityListener
        {
            ShouldListenTo = s => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(actListener);

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instr, l) => l.EnableMeasurementEvents(instr);
        meterListener.Start();

        var settings = new SharpS3Settings();
        using var serverTelemetry = new S3ServerTelemetry(new S3ServerTelemetrySettings { IncludeBucketNames = true, IncludeObjectKeys = true }, () => settings, () => true);
        var reqTel = serverTelemetry.BeginRequest()!;
        reqTel.Should().NotBeNull();

        // 1. StartCallback
        using var cbStage = reqTel.StartCallback("GetBucket");
        cbStage.Should().NotBeNull();

        // 2. Describe with activity
        var req = new S3Request(
            "GET",
            "/bucket/key?uploadId=u123",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>(),
            Stream.Null
        );
        req.RequestType = S3RequestType.ObjectRead;
        req.Bucket = "my-bucket";
        req.Key = "my-key";
        req.UploadId = "u123";
        req.PartNumber = 1;
        req.SignatureVersion = S3SignatureVersion.Version2;

        reqTel.Describe(req);
        reqTel.Describe(null!); // Null safety check

        // 3. MarkResponseError
        reqTel.MarkResponseError(ErrorCode.NotModified); // Should return early without recording
        reqTel.MarkResponseError(ErrorCode.NoSuchKey);

        // 4. MarkException
        var ex = new Exception("simulated error");
        reqTel.MarkException(ex, S3ServerTelemetryNames.StageInternal);

        // 5. Complete with 500 error
        reqTel.Complete(500);

        // 6. Complete idempotency (second complete is ignored)
        reqTel.Complete(500);

        // 7. SignatureVersionOf variations
        RequestTelemetry.SignatureVersionOf(null).Should().Be(S3ServerTelemetryNames.SignatureVersionNone);

        var rV4 = new S3Request("GET", "/", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);
        rV4.SignatureVersion = S3SignatureVersion.Version4;
        RequestTelemetry.SignatureVersionOf(rV4).Should().Be(S3ServerTelemetryNames.SignatureVersion4);

        var rV2 = new S3Request("GET", "/", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);
        rV2.SignatureVersion = S3SignatureVersion.Version2;
        RequestTelemetry.SignatureVersionOf(rV2).Should().Be(S3ServerTelemetryNames.SignatureVersion2);

        var rUnk = new S3Request("GET", "/", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);
        rUnk.SignatureVersion = S3SignatureVersion.Unknown;
        RequestTelemetry.SignatureVersionOf(rUnk).Should().Be(S3ServerTelemetryNames.SignatureVersionNone);
    }

    [TestMethod]
    public void S3ServerTelemetry_Disposed_NullMeter_And_ElapsedSeconds_Branches()
    {
        // 1. ElapsedSeconds future and past timestamps
        long now = Stopwatch.GetTimestamp();
        S3ServerTelemetry.ElapsedSeconds(now + 100_000_000).Should().Be(0);
        S3ServerTelemetry.ElapsedSeconds(now - 1000).Should().BeGreaterThan(0);

        // 2. Disabled telemetry (all null instruments)
        var disabledSettings = new S3ServerTelemetrySettings { Enable = false };
        var serverSettings = new SharpS3Settings();
        var telemetry = new S3ServerTelemetry(disabledSettings, () => serverSettings, () => true);

        telemetry.Meter.Should().BeNull();
        telemetry.Source.Should().BeNull();

        // Null meter guard branches
        telemetry.RecordRequest("GetObject", "Success", "Handler", "NoSuchKey", 0.1);
        telemetry.RecordStage("stage", "Success", 0.01);
        telemetry.RecordCallback("cb", "Success", "err", 0.02);
        telemetry.RecordSignature("V4", "Success", 0.005);
        telemetry.RecordError("err", "stage");
        telemetry.RecordObjectSize("Put", 1024);
        telemetry.RecordLifecycle("Started");
        telemetry.AddActive(1).Should().BeFalse();

        // BeginRequest with disabled telemetry returns null
        telemetry.BeginRequest().Should().BeNull();

        // Dispose when instruments are null
        telemetry.Dispose();

        // Double dispose covers if (_Disposed) return;
        telemetry.Dispose();

        // BeginRequest when disposed covers if (_Disposed) return null;
        telemetry.BeginRequest().Should().BeNull();

        // 3. Traces-only telemetry
        using var actListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "TracesOnlySource",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(actListener);

        var tracesOnlySettings = new S3ServerTelemetrySettings
        {
            ActivitySourceName = "TracesOnlySource",
            EnableMetrics = false,
            EnableTraces = true
        };
        var tracesOnlyTelemetry = new S3ServerTelemetry(tracesOnlySettings, () => serverSettings, () => true);
        var reqFromTraces = tracesOnlyTelemetry.BeginRequest();
        reqFromTraces.Should().NotBeNull();
        reqFromTraces!.Complete(200);
        tracesOnlyTelemetry.Dispose();

        // 4. Metrics-only telemetry
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == "MetricsOnlyMeter")
            {
                l.EnableMeasurementEvents(instr);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, _, _, _) => { });
        meterListener.SetMeasurementEventCallback<double>((_, _, _, _) => { });
        meterListener.Start();

        var metricsOnlySettings = new S3ServerTelemetrySettings
        {
            MeterName = "MetricsOnlyMeter",
            EnableMetrics = true,
            EnableTraces = false
        };
        var metricsOnlyTelemetry = new S3ServerTelemetry(metricsOnlySettings, () => serverSettings, () => true);
        var reqFromMetrics = metricsOnlyTelemetry.BeginRequest();
        reqFromMetrics.Should().NotBeNull();
        reqFromMetrics!.Complete(200);
        metricsOnlyTelemetry.Dispose();
    }

    [TestMethod]
    public void S3ServerTelemetry_ResolveLibraryVersion_AllBranches()
    {
        // Default assembly
        S3ServerTelemetry.ResolveLibraryVersion().Should().NotBeNullOrEmpty();
        S3ServerTelemetry.ResolveLibraryVersion(null).Should().NotBeNullOrEmpty();

        // Assembly with version fallback (infoVersion is null)
        var asmWithVersion = new MockVersionAssembly(null, new Version(3, 4, 5));
        S3ServerTelemetry.ResolveLibraryVersion(asmWithVersion).Should().Be("3.4.5");

        // Assembly with no version at all (infoVersion and version are null) -> returns "unknown"
        var asmUnknown = new MockVersionAssembly(null, null);
        S3ServerTelemetry.ResolveLibraryVersion(asmUnknown).Should().Be("unknown");

        // Assembly with informational version containing '+'
        var asmWithPlus = new MockVersionAssembly("1.2.3+build456", null);
        S3ServerTelemetry.ResolveLibraryVersion(asmWithPlus).Should().Be("1.2.3");

        // Assembly with informational version without '+'
        var asmWithoutPlus = new MockVersionAssembly("2.0.1", null);
        S3ServerTelemetry.ResolveLibraryVersion(asmWithoutPlus).Should().Be("2.0.1");

        // Assembly that throws on GetCustomAttributes -> returns "unknown"
        var asmThrowing = new ThrowingVersionAssembly();
        S3ServerTelemetry.ResolveLibraryVersion(asmThrowing).Should().Be("unknown");

        // Assembly where GetName() returns null -> returns "unknown" (covers line 450 null branch)
        var asmNullName = new MockVersionAssembly(null, null, nullAssemblyName: true);
        S3ServerTelemetry.ResolveLibraryVersion(asmNullName).Should().Be("unknown");
    }

    [TestMethod]
    public void S3ServerTelemetry_Constructor_NullSettings_UsesDefault()
    {
        using var telemetry = new S3ServerTelemetry(null!, () => new SharpS3Settings(), () => true);
        telemetry.Settings.Should().NotBeNull();
        telemetry.Settings.Enable.Should().BeTrue();
    }

    [TestMethod]
    public void RequestTelemetry_UncoveredBranches_And_CatchBlocks()
    {
        // 0. Lines 79-82: Constructor catch block when startActivity is true but telemetry.Source is null
        var tracesDisabledSettings = new S3ServerTelemetrySettings { EnableTraces = false, EnableMetrics = true };
        using var tracesDisabledTelemetry = new S3ServerTelemetry(tracesDisabledSettings, () => new SharpS3Settings(), () => true);
        var reqThrowingConstructor = new RequestTelemetry(tracesDisabledTelemetry, startActivity: true);
        reqThrowingConstructor.Activity.Should().BeNull();

        // 1. Lines 103-105 & 115-118: StartStage & StartCallback catch blocks
        var uninit = (RequestTelemetry)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RequestTelemetry));
        uninit.StartStage("test-stage").Should().BeNull();
        uninit.StartCallback("test-callback").Should().BeNull();

        // 2. Line 129: Describe early return when _Activity == null
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == "RequestTelemetryBranchMeter") l.EnableMeasurementEvents(instr);
        };
        meterListener.Start();

        var metricsOnlySettings = new S3ServerTelemetrySettings
        {
            MeterName = "RequestTelemetryBranchMeter",
            EnableTraces = false,
            EnableMetrics = true
        };
        using var metricsOnlyTelemetry = new S3ServerTelemetry(metricsOnlySettings, () => new SharpS3Settings(), () => true);
        var reqWithoutActivity = metricsOnlyTelemetry.BeginRequest()!;
        reqWithoutActivity.Should().NotBeNull();
        reqWithoutActivity.Activity.Should().BeNull();
        reqWithoutActivity.Telemetry.Should().BeSameAs(metricsOnlyTelemetry);

        var s3Req = new S3Request("GET", "/test-bucket/test-key", null, null, Stream.Null);
        s3Req.RequestType = S3RequestType.ObjectRead;
        reqWithoutActivity.Describe(s3Req);
        reqWithoutActivity.Operation.Should().Be(S3RequestType.ObjectRead.ToString());

        // 3. Lines 150-152: Describe catch block (throws on _Telemetry.Settings because _Telemetry is null)
        var uninitForDescribe = (RequestTelemetry)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RequestTelemetry));
        typeof(RequestTelemetry).GetField("_Activity", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(uninitForDescribe, new Activity("test-describe-activity"));
        Action actDescribe = () => uninitForDescribe.Describe(s3Req);
        actDescribe.Should().NotThrow();

        // 4. Line 177: MarkException early return when e == null
        reqWithoutActivity.MarkException(null!, S3ServerTelemetryNames.StageParse);

        // 5. Line 184 & Lines 197-199: RecordObjectSize
        reqWithoutActivity.RecordObjectSize(4096);
        reqWithoutActivity.RecordObjectSize(-100);
        uninit.RecordObjectSize(1024); // Safe catch when _Telemetry is null (hits lines 197-199)

        // 6. Lines 234-235: Complete catch block
        var uninitForComplete = (RequestTelemetry)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RequestTelemetry));
        Action actComplete = () => uninitForComplete.Complete(500);
        actComplete.Should().NotThrow();

        // 7. StartCurrentCallback (detached and attached)
        RequestTelemetry.StartCurrentCallback("DetachedCallback").Should().BeNull();
        RequestTelemetry.Current = reqWithoutActivity;
        using (var cbStage = RequestTelemetry.StartCurrentCallback("AttachedCallback"))
        {
            cbStage.Should().NotBeNull();
        }
        RequestTelemetry.Current = null;

        // 8. Properties and client error Complete path
        reqWithoutActivity.Handler = S3ServerTelemetryNames.HandlerCallback;
        reqWithoutActivity.Handler.Should().Be(S3ServerTelemetryNames.HandlerCallback);
        reqWithoutActivity.SignatureOutcome = "valid";
        reqWithoutActivity.SignatureOutcome.Should().Be("valid");
        reqWithoutActivity.Complete(404);

        // 9. Server error Complete path without exception
        var req500 = metricsOnlyTelemetry.BeginRequest()!;
        req500.Complete(500);

        // 10. TelemetryStage double dispose (hits line 110: if (_Disposed) return;)
        using (var doubleDisposeStage = reqWithoutActivity.StartStage("double-dispose-stage"))
        {
            doubleDisposeStage.Should().NotBeNull();
            doubleDisposeStage!.Dispose();
            doubleDisposeStage.Dispose(); // second call
        }

        // 11. RequestTelemetry MarkResponseError with unexpected exception and InternalError (hits line 179)
        var reqInternalErr = metricsOnlyTelemetry.BeginRequest()!;
        reqInternalErr.MarkException(new InvalidOperationException("unexpected error"), S3ServerTelemetryNames.StageParse);
        reqInternalErr.MarkResponseError(ErrorCode.InternalError);
        var errField = typeof(RequestTelemetry).GetField("_ErrorType", BindingFlags.NonPublic | BindingFlags.Instance)!;
        errField.GetValue(reqInternalErr).Should().Be("System.InvalidOperationException");
    }

    private sealed class MockVersionAssembly : Assembly
    {
        private readonly string? _infoVersion;
        private readonly Version? _version;
        private readonly bool _nullAssemblyName;

        public MockVersionAssembly(string? infoVersion, Version? version, bool nullAssemblyName = false)
        {
            _infoVersion = infoVersion;
            _version = version;
            _nullAssemblyName = nullAssemblyName;
        }

        public override object[] GetCustomAttributes(Type attributeType, bool inherit)
        {
            if (attributeType == typeof(AssemblyInformationalVersionAttribute) && _infoVersion != null)
            {
                return new Attribute[] { new AssemblyInformationalVersionAttribute(_infoVersion) };
            }
            return Array.Empty<Attribute>();
        }

        public override AssemblyName GetName()
        {
            if (_nullAssemblyName) return null!;
            return new AssemblyName("MockAssembly") { Version = _version };
        }
    }

    private sealed class ThrowingVersionAssembly : Assembly
    {
        public override object[] GetCustomAttributes(Type attributeType, bool inherit)
        {
            throw new InvalidOperationException("Failed to get attributes");
        }
    }
}
