using System.Dynamic;
using MeduzaRepost;
using Microsoft.CSharp.RuntimeBinder;

namespace Tests;

[TestFixture]
public class Misc
{
    [Test]
    public void DynamicCast()
    {
        var test1 = new TestClassWithPts { pts = 42 };
        var test2 = new TestClassWithoutPts { prop = 69 };
        var dyn = (dynamic)test1;
        Assert.That(dyn.pts, Is.EqualTo(42));
        Assert.Throws<RuntimeBinderException>(() => { var result = dyn.prop ?? 0; });

        Assert.Multiple(() =>
        {
            Assert.That(test1.GetPts(), Is.EqualTo(42));
            Assert.That(test2.GetPts(), Is.EqualTo(0));
        });
    }

    private class TestClassWithPts { public int pts; }
    private class TestClassWithoutPts { public int prop; }
}