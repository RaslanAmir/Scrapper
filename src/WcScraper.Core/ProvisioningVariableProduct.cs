using System;
using System.Collections.Generic;
using System.Linq;

namespace WcScraper.Core;

public sealed class ProvisioningVariableProduct
{
    public ProvisioningVariableProduct(StoreProduct parent, IEnumerable<StoreProduct> variations)
    {
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
        Variations = variations?
            .Where(v => v is not null)
            .Select(v => v!)
            .ToList()
            ?? new List<StoreProduct>();
    }

    public StoreProduct Parent { get; }

    public IReadOnlyList<StoreProduct> Variations { get; }
}
