using System.Runtime.InteropServices;
using KompasMcp.Domain.Com;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>INVARIANT (FIX C): an unread COM value must differ from a successfully read <c>false</c>/<c>0</c>/
/// first enum value. The old helper returned <c>default</c>, making "the read did not happen"
/// indistinguishable from "the default value was read". History: docs/decisions/tests.md#safe-read</summary>
public class SafeReadTests
{
    private enum Sample
    {
        First = 0,
        Second = 1,
    }

    private static T Fail<T>() => throw new COMException("чтение не состоялось");

    [Fact]
    public void UnreadBool_IsNull_WhileReadFalse_IsFalse()
    {
        Assert.Null(SafeRead.Bool(() => Fail<bool>()));
        Assert.False(SafeRead.Bool(() => false));
        Assert.True(SafeRead.Bool(() => true));
    }

    [Fact]
    public void UnreadNullableBool_IsNull_WhileReadNull_IsNull()
    {
        // Both null — expected for reference semantics; the difference is caught through TryRead.
        Assert.Null(SafeRead.Bool(() => Fail<bool?>()));
        var read = SafeRead.TryRead(() => Fail<bool>());
        Assert.False(read.Ok);
    }

    [Fact]
    public void UnreadInt_IsNull_WhileReadZero_IsZero()
    {
        Assert.Null(SafeRead.Int(() => Fail<int>()));
        Assert.Equal(0, SafeRead.Int(() => 0));
    }

    [Fact]
    public void UnreadEnumName_IsNamedUnread_NotTheFirstEnumValue()
    {
        // The old Safe gave default(Sample) = First, and "not read" was printed as "First".
        Assert.Equal("unread", SafeRead.EnumName(() => Fail<Sample>()));
        Assert.Equal("First", SafeRead.EnumName(() => Sample.First));
        Assert.Equal("Second", SafeRead.EnumName(() => Sample.Second));
    }

    [Fact]
    public void UnreadEnumOrNull_IsNull_WhileReadFirst_IsFirst()
    {
        Assert.Null(SafeRead.EnumOrNull<Sample>(() => Fail<Sample?>()));
        Assert.Equal(Sample.First, SafeRead.EnumOrNull<Sample>(() => Sample.First));
    }

    [Fact]
    public void UnreadReference_IsNull()
    {
        Assert.Null(SafeRead.Ref<string>(() => Fail<string>()));
        Assert.Null(SafeRead.Text(() => Fail<string?>()));
        Assert.Equal("ok", SafeRead.Text(() => "ok"));
    }

    [Fact]
    public void TryRead_CarriesTheOkFlag_AndTheValue()
    {
        var read = SafeRead.TryRead(() => 7);
        Assert.True(read.Ok);
        Assert.Equal(7, read.Value);

        var failed = SafeRead.TryRead(() => Fail<int>());
        Assert.False(failed.Ok);
    }

    [Fact]
    public void NonFiniteDouble_IsNotAReadNumber()
    {
        Assert.Null(SafeRead.Double(() => double.NaN));
        Assert.Null(SafeRead.Double(() => double.PositiveInfinity));
        Assert.Equal(2.5, SafeRead.Double(() => 2.5));
    }
}
