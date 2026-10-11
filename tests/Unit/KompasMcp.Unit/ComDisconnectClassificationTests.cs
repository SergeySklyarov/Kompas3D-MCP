using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KompasMcp.Contracts;
using KompasMcp.Domain.Com;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The classification of a COM connection loss, and the session-level latch built on it.</summary>
/// <remarks>WHY THESE TESTS EXIST. MEASURED: a killed KOMPAS made `kompas_list_components` answer
/// `succeeded` with an empty list and `kompas_get_context` answer `succeeded` with -1/0, because the
/// adapter's local catches swallowed the loss and the classifier did not recognise the client-side code
/// the loss arrives as. The class lives in Domain (no KOMPAS types), where it can be EXECUTED — a
/// classification read only as source text is a claim about text. The values are not restated from
/// memory: they are cross-checked against the Windows SDK header, and that check FAILS when the header
/// is absent rather than skipping. History: docs/decisions/adapter-core.md#disconnect-classification</remarks>
public class ComDisconnectClassificationTests
{
    /// <summary>HRESULTs that mean "a live server can no longer be reached".</summary>
    public static readonly (int Code, string Name)[] DisconnectCodes =
    {
        (ComHResult.RPC_E_DISCONNECTED, "RPC_E_DISCONNECTED"),
        (ComHResult.RPC_E_SERVER_DIED, "RPC_E_SERVER_DIED"),
        (ComHResult.RPC_E_SERVER_DIED_DNE, "RPC_E_SERVER_DIED_DNE"),
        (ComHResult.RPC_S_SERVER_UNAVAILABLE, "RPC_S_SERVER_UNAVAILABLE"),
        (ComHResult.RPC_S_CALL_FAILED, "RPC_S_CALL_FAILED"),
    };

    /// <summary>Codes that are NOT a lost connection — the negative half, without which a classifier
    /// answering "disconnected" to everything would pass.</summary>
    public static readonly (int Code, string What)[] NonDisconnectCodes =
    {
        // The value the former constant carried under the name RPC_E_SERVERDIED. winerror.h calls it
        // RPC_E_VERSION_MISMATCH; treating a protocol-version clash as a dead server was the defect.
        (unchecked((int)0x80010110), "RPC_E_VERSION_MISMATCH"),
        // A refused LAUNCH keeps its own classification (LICENSE_UNAVAILABLE); folding it in would
        // silently reclassify every failed start.
        (ComHResult.CO_E_SERVER_EXEC_FAILURE, "CO_E_SERVER_EXEC_FAILURE"),
        (ComHResult.RPC_E_SERVERCALL_RETRYLATER, "RPC_E_SERVERCALL_RETRYLATER"),
        (ComHResult.RPC_E_SERVERBUSY, "RPC_E_SERVERBUSY"),
        (ComHResult.REGDB_E_CLASSNOTREG, "REGDB_E_CLASSNOTREG"),
        (ComHResult.S_OK, "S_OK"),
        (unchecked((int)0x80004005), "E_FAIL"),
    };

    public static TheoryData<int, string> DisconnectRows()
    {
        var data = new TheoryData<int, string>();
        foreach (var (code, name) in DisconnectCodes)
        {
            data.Add(code, name);
        }

        return data;
    }

    public static TheoryData<int, string> NonDisconnectRows()
    {
        var data = new TheoryData<int, string>();
        foreach (var (code, what) in NonDisconnectCodes)
        {
            data.Add(code, what);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DisconnectRows))]
    public void DisconnectCode_IsRecognisedAndNamed(int code, string name)
    {
        Assert.True(ComHResult.IsDisconnected(code), $"0x{code:X8} должен признаваться обрывом связи");
        Assert.Equal(name, ComHResult.Name(code));
    }

    [Theory]
    [MemberData(nameof(NonDisconnectRows))]
    public void NonDisconnectCode_IsNotLaunderedIntoADisconnect(int code, string what)
    {
        Assert.False(ComHResult.IsDisconnected(code),
            $"0x{code:X8} ({what}) не означает потерю связи — признание его обрывом меняет классификацию отказов");

        // The LABEL matters as much as the predicate: it is what a client reads in the refusal. A code
        // outside the table is reported verbatim (its own hex), never under a loss name.
        var label = ComHResult.Name(code);
        foreach (var (_, lossName) in DisconnectCodes)
        {
            Assert.NotEqual(lossName, label);
        }
    }

    [Fact]
    public void SeparatedRcw_CountsAsALostConnection()
    {
        // An RCW whose underlying object is gone throws InvalidComObjectException, which carries no COM
        // HRESULT at all; the exception overload must still call it a loss.
        var separated = new InvalidComObjectException("COM object that has been separated from its RCW");

        Assert.True(ComHResult.IsDisconnected(separated));
        Assert.Equal(unchecked((int)0x80131501), ComHResult.From(separated));
    }

    [Fact]
    public void ExceptionOverload_UsesTheSamePredicateAsTheCodeOverload()
    {
        foreach (var (code, _) in DisconnectCodes)
        {
            Assert.True(ComHResult.IsDisconnected(new COMException("loss", code)));
        }

        foreach (var (code, _) in NonDisconnectCodes)
        {
            Assert.False(ComHResult.IsDisconnected(new COMException("other", code)));
        }

        // A managed exception with no HRESULT is not a loss.
        Assert.False(ComHResult.IsDisconnected(new InvalidOperationException("not a COM failure")));
    }

    [Fact]
    public void EveryCodeTheClassifierRecognisesMatchesTheWindowsSdkHeader()
    {
        var header = ReadWinErrorHeader();
        var declared = new (string Symbol, int Value)[]
        {
            ("RPC_E_DISCONNECTED", ComHResult.RPC_E_DISCONNECTED),
            ("RPC_E_SERVER_DIED", ComHResult.RPC_E_SERVER_DIED),
            ("RPC_E_SERVER_DIED_DNE", ComHResult.RPC_E_SERVER_DIED_DNE),
            ("RPC_S_SERVER_UNAVAILABLE", ComHResult.RPC_S_SERVER_UNAVAILABLE),
            ("RPC_S_CALL_FAILED", ComHResult.RPC_S_CALL_FAILED),
            ("CO_E_SERVER_EXEC_FAILURE", ComHResult.CO_E_SERVER_EXEC_FAILURE),
            ("RPC_E_VERSION_MISMATCH", unchecked((int)0x80010110)),
        };

        foreach (var (symbol, value) in declared)
        {
            var published = WinErrorValue(header, symbol);
            Assert.True(published.HasValue,
                $"символ {symbol} не найден в {header} — число классификации нельзя подтвердить");
            Assert.Equal(published!.Value, value);
        }
    }

    /// <summary>The header's value for one symbol, as an HRESULT. A Win32 error number (the RPC_S_*
    /// family) becomes an HRESULT by the documented <c>0x80070000 | n</c>.</summary>
    private static int? WinErrorValue(string path, string symbol)
    {
        var text = File.ReadAllText(path);
        var match = Regex.Match(
            text,
            @"^#define\s+" + symbol + @"\s+(_HRESULT_TYPEDEF_\((?<hx>0x[0-9A-Fa-f]+)L\)|(?<win>\d+)L)\s*$",
            RegexOptions.Multiline);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups["hx"].Success
            ? unchecked((int)Convert.ToUInt32(match.Groups["hx"].Value, 16))
            : unchecked((int)(0x80070000u | Convert.ToUInt32(match.Groups["win"].Value)));
    }

    /// <summary>Path to the newest <c>winerror.h</c> of the installed Windows SDK. Absence is a REFUSAL,
    /// not a skip: a guard that silently does nothing is indistinguishable from one that passed.</summary>
    private static string ReadWinErrorHeader()
    {
        var candidates = new List<string>();
        foreach (var root in ProgramFilesRoots())
        {
            var include = Path.Combine(root, "Windows Kits", "10", "Include");
            if (!Directory.Exists(include))
            {
                continue;
            }

            candidates.AddRange(Directory.EnumerateDirectories(include)
                .Select(version => Path.Combine(version, "shared", "winerror.h"))
                .Where(File.Exists));
        }

        Assert.True(candidates.Count > 0,
            "winerror.h не найден: числа классификации обрыва нельзя подтвердить по документации Windows. " +
            "Установите Windows SDK (Include\\<версия>\\shared\\winerror.h) — молча пропустить проверку нельзя.");

        return candidates.OrderByDescending(VersionOf).First();

        static string VersionOf(string path) =>
            Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))!) ?? string.Empty;
    }

    /// <summary>Every plausible Program Files root. The API is consulted FIRST because this shell does not
    /// export <c>ProgramFilesX86</c>/<c>ProgramFiles</c> at all, and a lookup that only reads the
    /// environment would refuse on a machine that plainly has the SDK.</summary>
    private static IEnumerable<string> ProgramFilesRoots()
    {
        var seen = new List<string>();
        foreach (var path in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetEnvironmentVariable("ProgramFilesX86"),
                     Environment.GetEnvironmentVariable("ProgramFiles"),
                 })
        {
            if (!string.IsNullOrWhiteSpace(path) && !seen.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                seen.Add(path);
            }
        }

        return seen;
    }
}

/// <summary>The session-level latch that carries the first observed connection loss.</summary>
public class ComDisconnectLatchTests
{
    private static COMException Loss(int code) => new("connection lost", code);

    /// <summary>A latch with the observation window OPEN on this thread, as during a session command.</summary>
    private static ComDisconnectLatch Armed()
    {
        var latch = new ComDisconnectLatch();
        latch.Arm(Environment.CurrentManagedThreadId);
        return latch;
    }

    [Fact]
    public void FirstLossIsRecordedWithItsCodeAndName()
    {
        var latch = Armed();
        Assert.False(latch.IsSet);
        Assert.Null(latch.Observation);

        Assert.True(latch.Observe(Loss(ComHResult.RPC_S_SERVER_UNAVAILABLE)));

        Assert.True(latch.IsSet);
        Assert.Equal(ComHResult.RPC_S_SERVER_UNAVAILABLE, latch.Observation!.HResult);
        Assert.Equal("RPC_S_SERVER_UNAVAILABLE", latch.Observation.Name);
        Assert.Equal("COMException", latch.Observation.ExceptionType);
    }

    [Fact]
    public void TheFirstLossWins_ALaterSwallowedFailureDoesNotReplaceIt()
    {
        // The first failure names the call that actually broke; the rest are its consequences.
        var latch = Armed();
        latch.Observe(Loss(ComHResult.RPC_E_DISCONNECTED));
        latch.Observe(Loss(ComHResult.RPC_S_CALL_FAILED));

        Assert.Equal(ComHResult.RPC_E_DISCONNECTED, latch.Observation!.HResult);
    }

    [Fact]
    public void AnOrdinaryFailureDoesNotSetTheLatch()
    {
        var latch = Armed();

        Assert.False(latch.Observe(Loss(ComHResult.RPC_E_SERVERBUSY)));
        Assert.False(latch.Observe(new InvalidOperationException("not COM")));
        Assert.False(latch.Observe(null));

        Assert.False(latch.IsSet);
        Assert.Equal(0, latch.IgnoredOutsideSession);
    }

    [Fact]
    public void ALossOutsideASessionCommandIsSeenAndNOTApplied()
    {
        // MEASURED CASE: `kompas_capabilities` enumerates the ROT and binds a stale entry of a DEAD
        // FOREIGN instance; that bind raises the very same class of error. It must not mark OUR session,
        // and the count must show it WAS seen — else "the mark is clear" and "nothing was seen" look alike.
        var latch = new ComDisconnectLatch();

        Assert.False(latch.Observe(Loss(ComHResult.RPC_S_SERVER_UNAVAILABLE)));
        Assert.False(latch.IsSet);
        Assert.Equal(1, latch.IgnoredOutsideSession);

        // A loss during a session command is still recorded after that.
        latch.Arm(Environment.CurrentManagedThreadId);
        Assert.True(latch.Observe(Loss(ComHResult.RPC_E_DISCONNECTED)));
        Assert.True(latch.IsSet);
        Assert.Equal(1, latch.IgnoredOutsideSession);
    }

    [Fact]
    public void ALossOnAnotherThreadIsNotOursAndIsNotCountedAsForeign()
    {
        // MEASURED: an exception thrown on the STA thread inside a command is RE-THROWN on the awaiting
        // thread when the task is awaited, so the same loss reaches the observer twice. The second sighting
        // must be neither applied NOR counted as a foreign loss — counting it inflated the counter with
        // our own losses and made a real foreign loss indistinguishable from a re-throw.
        var latch = new ComDisconnectLatch();
        latch.Arm(Environment.CurrentManagedThreadId + 1000);

        Assert.False(latch.Observe(Loss(ComHResult.RPC_S_SERVER_UNAVAILABLE)));
        Assert.False(latch.IsSet);
        Assert.Equal(0, latch.IgnoredOutsideSession);
    }

    [Fact]
    public void TheWindowIsNestedAndClosesOnlyWithTheLastCommand()
    {
        var latch = new ComDisconnectLatch();
        latch.Arm(Environment.CurrentManagedThreadId);
        latch.Arm(Environment.CurrentManagedThreadId);

        latch.Disarm();
        Assert.True(latch.Observe(Loss(ComHResult.RPC_E_DISCONNECTED)));

        latch.Disarm();
        latch.Disarm();
        Assert.False(latch.Observe(Loss(ComHResult.RPC_S_CALL_FAILED)));

        // The last sighting happened with the window SHUT: it counts as an out-of-session loss.
        Assert.Equal(1, latch.IgnoredOutsideSession);
    }

    [Fact]
    public void ClearWithReasonLiftsTheMarkAndKeepsTheReason()
    {
        // OUR application answered a cheap call: the loss belonged to a foreign object.
        var latch = Armed();
        latch.Observe(Loss(ComHResult.RPC_S_SERVER_UNAVAILABLE));
        Assert.True(latch.IsSet);

        latch.ClearWithReason("наше приложение ответило");

        Assert.False(latch.IsSet);
        Assert.Equal("наше приложение ответило", latch.ClearedReason);
    }

    [Fact]
    public void ResetClearsTheMark()
    {
        // A successful kompas_connect clears it: the session the mark described is gone.
        var latch = Armed();
        latch.Observe(Loss(ComHResult.RPC_E_SERVER_DIED));
        Assert.True(latch.IsSet);

        latch.Reset();

        Assert.False(latch.IsSet);
        Assert.Null(latch.Observation);
    }
}

/// <summary>An unread counter is published as null WITH a reason, never as -1 or 0.</summary>
/// <remarks>MEASURED: `kompas_get_context` published -1 for the body count and 0 for the feature count
/// when the collection did not answer, so a client read "the model is empty" where nothing had been read.
/// History: docs/decisions/adapter-core.md#unread-counters</remarks>
public class UnreadCounterContractTests
{
    private static JsonObject Serialize(DocumentContextDto context) =>
        JsonNode.Parse(KompJson.Serialize(context))!.AsObject();

    private static DocumentContextDto Context(int? features, int? bodies, int? components,
        params string[] unverified) => new()
    {
        Id = "doc-1",
        ApplicationId = "app-1",
        Kind = DocumentKind.Part,
        Dirty = false,
        Revision = 1,
        FeatureCount = features,
        BodyCount = bodies,
        ComponentCount = components,
        UnitSystem = "mm",
        ExternalChangeDetection = ExternalChangeDetection.Conservative,
        UnverifiedAspects = unverified,
    };

    [Fact]
    public void AnUnreadCounterIsNull_NotMinusOneAndNotZero()
    {
        var node = Serialize(Context(
            features: null,
            bodies: null,
            components: 0,
            "feature_count_unread — коллекция признаков КОМПАС не ответила",
            "body_count_unread — коллекция тел КОМПАС не ответила"));

        Assert.True(node.ContainsKey("feature_count"));
        Assert.Null(node["feature_count"]);
        Assert.Null(node["body_count"]);

        // A genuinely measured zero stays a zero: "read as none" and "not read" must not share a value.
        Assert.Equal(0, node["component_count"]!.GetValue<int>());
    }

    [Fact]
    public void EveryNullCounterCarriesItsReason()
    {
        var node = Serialize(Context(
            features: null,
            bodies: null,
            components: null,
            "feature_count_unread",
            "body_count_unread",
            "component_count_unread"));

        var aspects = node["unverified_aspects"]!.AsArray()
            .Select(item => item!.GetValue<string>())
            .ToArray();

        Assert.Equal(3, aspects.Length);
        Assert.Contains("feature_count_unread", aspects);
        Assert.Contains("body_count_unread", aspects);
        Assert.Contains("component_count_unread", aspects);
    }

    [Fact]
    public void MeasuredCountersCarryNoReason()
    {
        var node = Serialize(Context(features: 7, bodies: 2, components: 1));

        Assert.Equal(7, node["feature_count"]!.GetValue<int>());
        Assert.Equal(2, node["body_count"]!.GetValue<int>());
        Assert.Equal(1, node["component_count"]!.GetValue<int>());
        Assert.Empty(node["unverified_aspects"]!.AsArray());
    }
}
