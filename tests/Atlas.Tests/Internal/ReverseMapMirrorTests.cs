using System.Reflection;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class ReverseMapMirrorTests
{
    public sealed class Customer { public string? Name { get; set; } public string? Email { get; set; } }
    public sealed class Address { public string? City { get; set; } }
    public sealed class CustomerWithAddress { public Address? HomeAddress { get; set; } }
    public sealed class Order { public int Id { get; set; } public Customer? Customer { get; set; } public decimal Total { get; set; } }
    public sealed class OrderDeep { public CustomerWithAddress? Customer { get; set; } }
    public sealed class OrderDto { public string? CustomerName { get; set; } public string? CustomerEmail { get; set; } public decimal Total { get; set; } public int Id { get; set; } }
    public sealed class OrderDeepDto { public string? CustomerHomeAddressCity { get; set; } }

    private static (TypeMap fwd, TypeMap rev) BuildPair(Type srcType, Type dstType)
    {
        var fwd = new TypeMap(srcType, dstType, MemberList.None);
        var rev = new TypeMap(dstType, srcType, MemberList.None) { ReverseMapPair = fwd.Pair };
        return (fwd, rev);
    }

    private static PropertyInfo P(Type t, string name) => t.GetProperty(name)!;

    [Fact]
    public void Mirror_SingleLevelConvention_FlipsToSingleLevelOnReverse()
    {
        var (fwd, rev) = BuildPair(typeof(Order), typeof(OrderDto));
        var fwdPm = PropertyMap.ForProperty(P(typeof(OrderDto), "Total"));
        fwdPm.SourcePath = new SourceMemberPath(new[] { P(typeof(Order), "Total") });
        fwd.PropertyMaps.Add(fwdPm);

        ReverseMapMirror.Mirror(new[] { fwd, rev });

        var revPm = rev.PropertyMaps.Single(p => p.Name == "Total");
        Assert.Null(revPm.DestinationPath);
        Assert.Equal(P(typeof(Order), "Total"), revPm.DestinationProperty);
        Assert.NotNull(revPm.SourcePath);
        Assert.Equal("Total", revPm.SourcePath!.Members.Single().Name);
    }

    [Fact]
    public void Mirror_TwoLevelChain_FlipsToUnflattenPath()
    {
        var (fwd, rev) = BuildPair(typeof(Order), typeof(OrderDto));
        var fwdPm = PropertyMap.ForProperty(P(typeof(OrderDto), "CustomerName"));
        fwdPm.SourcePath = new SourceMemberPath(new[] { P(typeof(Order), "Customer"), P(typeof(Customer), "Name") });
        fwd.PropertyMaps.Add(fwdPm);

        ReverseMapMirror.Mirror(new[] { fwd, rev });

        var revPm = rev.PropertyMaps.Single(p => p.Name == "Customer.Name");
        Assert.NotNull(revPm.DestinationPath);
        Assert.Collection(revPm.DestinationPath!,
            p => Assert.Equal("Customer", p.Name),
            p => Assert.Equal("Name", p.Name));
        Assert.NotNull(revPm.SourcePath);
        Assert.Equal("CustomerName", revPm.SourcePath!.Members.Single().Name);
    }

    [Fact]
    public void Mirror_ThreeLevelChain_FlipsToThreeLevelUnflattenPath()
    {
        var (fwd, rev) = BuildPair(typeof(OrderDeep), typeof(OrderDeepDto));
        var fwdPm = PropertyMap.ForProperty(P(typeof(OrderDeepDto), "CustomerHomeAddressCity"));
        fwdPm.SourcePath = new SourceMemberPath(new[]
        {
            P(typeof(OrderDeep), "Customer"),
            P(typeof(CustomerWithAddress), "HomeAddress"),
            P(typeof(Address), "City"),
        });
        fwd.PropertyMaps.Add(fwdPm);

        ReverseMapMirror.Mirror(new[] { fwd, rev });

        var revPm = rev.PropertyMaps.Single(p => p.Name == "Customer.HomeAddress.City");
        Assert.NotNull(revPm.DestinationPath);
        Assert.Equal(3, revPm.DestinationPath!.Count);
    }

    [Fact]
    public void Mirror_ReverseExplicitBinding_NotOverwritten()
    {
        // Skip-rule-1 — exact name match.
        var (fwd, rev) = BuildPair(typeof(Order), typeof(OrderDto));
        var fwdPm = PropertyMap.ForProperty(P(typeof(OrderDto), "Total"));
        fwdPm.SourcePath = new SourceMemberPath(new[] { P(typeof(Order), "Total") });
        fwd.PropertyMaps.Add(fwdPm);

        // Pre-existing reverse binding for "Total" (e.g., user .ForMember(d => d.Total, opt => opt.Ignore())).
        var explicitRev = PropertyMap.ForProperty(P(typeof(Order), "Total"));
        explicitRev.Ignored = true;
        explicitRev.IsExplicit = true;
        rev.PropertyMaps.Add(explicitRev);

        ReverseMapMirror.Mirror(new[] { fwd, rev });

        Assert.Single(rev.PropertyMaps, p => p.Name == "Total");
        Assert.True(rev.PropertyMaps.Single(p => p.Name == "Total").Ignored);
    }

    [Fact]
    public void Mirror_UserExplicitTopLevelBinding_SuppressesMultiLevelMirror()
    {
        // Skip-rule-2 — user mapped Customer wholesale on the reverse.
        var (fwd, rev) = BuildPair(typeof(Order), typeof(OrderDto));
        var fwdPmName = PropertyMap.ForProperty(P(typeof(OrderDto), "CustomerName"));
        fwdPmName.SourcePath = new SourceMemberPath(new[] { P(typeof(Order), "Customer"), P(typeof(Customer), "Name") });
        fwd.PropertyMaps.Add(fwdPmName);
        var fwdPmEmail = PropertyMap.ForProperty(P(typeof(OrderDto), "CustomerEmail"));
        fwdPmEmail.SourcePath = new SourceMemberPath(new[] { P(typeof(Order), "Customer"), P(typeof(Customer), "Email") });
        fwd.PropertyMaps.Add(fwdPmEmail);

        // Pre-existing reverse binding for "Customer" with IsExplicit = true.
        var explicitRev = PropertyMap.ForProperty(P(typeof(Order), "Customer"));
        explicitRev.HasConstant = true;
        explicitRev.ConstantValue = new Customer { Name = "preset" };
        explicitRev.IsExplicit = true;
        rev.PropertyMaps.Add(explicitRev);

        ReverseMapMirror.Mirror(new[] { fwd, rev });

        // Mirror should NOT add Customer.Name or Customer.Email — they would overwrite the user's preset.
        Assert.DoesNotContain(rev.PropertyMaps, p => p.Name == "Customer.Name");
        Assert.DoesNotContain(rev.PropertyMaps, p => p.Name == "Customer.Email");
    }

    [Fact]
    public void Mirror_ForwardIgnored_NotMirrored()
    {
        var (fwd, rev) = BuildPair(typeof(Order), typeof(OrderDto));
        var fwdPm = PropertyMap.ForProperty(P(typeof(OrderDto), "Total"));
        fwdPm.SourcePath = new SourceMemberPath(new[] { P(typeof(Order), "Total") });
        fwdPm.Ignored = true;   // forward says ignore
        fwd.PropertyMaps.Add(fwdPm);

        ReverseMapMirror.Mirror(new[] { fwd, rev });

        Assert.Empty(rev.PropertyMaps);
    }

    [Fact]
    public void Mirror_ForwardCustomExpression_NotMirrored()
    {
        var (fwd, rev) = BuildPair(typeof(Order), typeof(OrderDto));
        var fwdPm = PropertyMap.ForProperty(P(typeof(OrderDto), "Total"));
        // Forward had .MapFrom(s => s.Total + 1) — non-invertible.
        fwdPm.CustomExpression = (System.Linq.Expressions.Expression<Func<Order, decimal>>)(s => s.Total + 1m);
        fwd.PropertyMaps.Add(fwdPm);

        ReverseMapMirror.Mirror(new[] { fwd, rev });

        Assert.Empty(rev.PropertyMaps);
    }

    [Fact]
    public void Mirror_ForwardConstant_NotMirrored()
    {
        var (fwd, rev) = BuildPair(typeof(Order), typeof(OrderDto));
        var fwdPm = PropertyMap.ForProperty(P(typeof(OrderDto), "Total"));
        fwdPm.HasConstant = true;
        fwdPm.ConstantValue = 99m;
        fwd.PropertyMaps.Add(fwdPm);

        ReverseMapMirror.Mirror(new[] { fwd, rev });

        Assert.Empty(rev.PropertyMaps);
    }

    public sealed class WriteOnlyDest
    {
        private string? _backing;
        public string? WriteOnly { set => _backing = value; }   // no getter
        public string? Visible => _backing;
    }
    public sealed class SimpleSrc { public string? Name { get; set; } }

    [Fact]
    public void Mirror_ForwardDestPropertyNoGetter_NotMirrored()
    {
        // Forward: SimpleSrc → WriteOnlyDest with PM targeting WriteOnlyDest.WriteOnly.
        // Mirror cannot use a no-getter dest as a reverse source — must skip.
        var (fwd, rev) = BuildPair(typeof(SimpleSrc), typeof(WriteOnlyDest));
        var writeOnlyProp = typeof(WriteOnlyDest).GetProperty("WriteOnly")!;
        var fwdPm = PropertyMap.ForProperty(writeOnlyProp);
        fwdPm.SourcePath = new SourceMemberPath(new[] { P(typeof(SimpleSrc), "Name") });
        fwd.PropertyMaps.Add(fwdPm);

        ReverseMapMirror.Mirror(new[] { fwd, rev });

        Assert.Empty(rev.PropertyMaps);
    }

    public sealed class GetterOnlySrc { public string ReadOnlyName { get; } = "fixed"; }
    public sealed class SettableDto { public string? Name { get; set; } }

    [Fact]
    public void Mirror_ForwardSourceLeafNoSetterOnReverseDest_NotMirrored()
    {
        // Forward: GetterOnlySrc.ReadOnlyName → SettableDto.Name (get-only forward source).
        // Reverse mirror would try to write to GetterOnlySrc.ReadOnlyName (no setter on the reverse
        // destination = the forward source). FlipBinding returns null for that — must skip.
        var (fwd, rev) = BuildPair(typeof(GetterOnlySrc), typeof(SettableDto));
        var fwdPm = PropertyMap.ForProperty(P(typeof(SettableDto), "Name"));
        fwdPm.SourcePath = new SourceMemberPath(new[] { P(typeof(GetterOnlySrc), "ReadOnlyName") });
        fwd.PropertyMaps.Add(fwdPm);

        ReverseMapMirror.Mirror(new[] { fwd, rev });

        Assert.Empty(rev.PropertyMaps);
    }
}
