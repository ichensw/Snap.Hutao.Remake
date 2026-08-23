// Copyright (c) DGP Studio. All rights reserved.
// Licensed under the MIT license.

namespace Snap.Hutao.Remastered.Service.MySqlSync;

using System;
using System.Collections.Generic;

public static class MySqlMetadataRows
{
    public sealed record EnumRow(int Value, string Lang, string Name);

    public static IEnumerable<EnumRow> CreateEnumRows<TEnum>(string lang)
        where TEnum : struct, Enum
    {
        foreach (TEnum value in Enum.GetValues<TEnum>())
        {
            int intValue = Convert.ToInt32(value);
            if (intValue is 0)
            {
                continue;
            }

            yield return new(intValue, lang, value.ToString());
        }
    }
}
