namespace KompasMcp.Domain.Documents;

/// <summary>What a lost answer to a document OPEN can be shown to be, from the liveness of the instance it
/// addressed: nothing was applied, the connection died after the call started, or neither is provable.</summary>
public enum OpenAttemptVerdict
{
    /// <summary>The instance's process was already gone before the call left: nothing could be opened, and
    /// the outcome is KNOWN — not applied.</summary>
    NotStarted,

    /// <summary>The process was alive before the call and is gone now: the loss happened while the call was
    /// under way, so the document may or may not have been opened — the outcome stays unknown.</summary>
    LostAfterStart,

    /// <summary>Neither fact was measured, so the outcome stays unknown with the reason named.</summary>
    Undetermined,
}

/// <summary>The single rule that turns the attempt evidence into a reconciliation verdict.</summary>
/// <remarks>INVARIANT: a dead process before dispatch PROVES that the open could not have happened, so the
/// answer is a definite "not applied" — not OUTCOME_UNKNOWN. MEASURED: an open after the attached instance
/// died answered OUTCOME_UNKNOWN with no document_id and no base revision, and reconciliation had nothing to
/// compare. A live process proves nothing in the other direction: the COM call may still fail, so that case
/// stays unknown with its reason named. History: docs/decisions/host.md#open-attempt-evidence</remarks>
public static class OpenAttemptPolicy
{
    /// <summary>The one code the definite "nothing was opened" answer carries.</summary>
    public const string NotStartedCode = "open_not_started";

    /// <summary>The one code the "connection lost after the call started" answer carries.</summary>
    public const string LostAfterStartCode = "open_lost_after_start";

    public static OpenAttemptVerdict Decide(
        bool? instanceAliveBeforeCall, bool? instanceAliveAtAnswer, out string reason)
    {
        if (instanceAliveBeforeCall == false)
        {
            reason = NotStartedCode + " — экземпляр КОМПАС был потерян ДО начала открытия: его процесс уже "
                + "не отвечал на проверку живости перед вызовом, поэтому открыть файл было нечем и "
                + "ничего не применилось. Исход определён, повтор с новым operation_id допустим после "
                + "kompas_connect.";
            return OpenAttemptVerdict.NotStarted;
        }

        if (instanceAliveBeforeCall == true && instanceAliveAtAnswer == false)
        {
            reason = LostAfterStartCode + " — экземпляр был ЖИВ перед вызовом и завершился во время него: "
                + "связь потеряна ПОСЛЕ начала открытия, и открылся ли файл, по этим сведениям не "
                + "установить. Исход остаётся неизвестным до согласования.";
            return OpenAttemptVerdict.LostAfterStart;
        }

        reason = instanceAliveBeforeCall is null
            ? "Живость экземпляра ДО вызова не измерена (журнал не нёс попытки открытия): доказать, что "
                + "открытие не начиналось, нечем — исход остаётся неизвестным."
            : "Экземпляр был жив перед вызовом, и живым он остался (или это не измерено): потеря связи "
                + "этим не доказана, а открытие могло и не начаться — исход остаётся неизвестным.";
        return OpenAttemptVerdict.Undetermined;
    }
}
