using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CodeSwitchX.Data;

/// <summary>Stores every DateTimeOffset as UTC ticks (a 64-bit integer) so SQLite can compare and index them and values round-trip exactly.</summary>
public sealed class UtcTicksConverter : ValueConverter<DateTimeOffset, long>
{
    public UtcTicksConverter()
        : base(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero))
    {
    }
}
