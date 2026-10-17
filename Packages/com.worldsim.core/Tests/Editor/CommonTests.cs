using System;
using System.Collections.Generic;
using NUnit.Framework;
using WorldSim.Core.Content;

namespace WorldSim.Core.Tests
{
    public class JsonTests
    {
        [Test]
        public void ParsesNestedDocument()
        {
            var o = (IDictionary<string, object>)Json.Parse("{\"a\": [1, 2.5, -3e2], \"b\": {\"c\": \"x\\ny\\u0041\"}, \"d\": true, \"e\": null}");
            var a = (IList<object>)o["a"];
            Assert.AreEqual(3, a.Count);
            Assert.AreEqual(-300.0, (double)a[2]);
            Assert.AreEqual("x\nyA", ((IDictionary<string, object>)o["b"])["c"]);
            Assert.AreEqual(true, o["d"]);
            Assert.IsNull(o["e"]);
        }

        [Test]
        public void SerializeRoundTrips()
        {
            var src = new Dictionary<string, object>
            {
                ["n"] = 0.1, ["s"] = "日本語\"q\"", ["l"] = new List<object> { 1.0, false },
            };
            var back = (IDictionary<string, object>)Json.Parse(Json.Serialize(src));
            Assert.AreEqual(0.1, back["n"]);
            Assert.AreEqual("日本語\"q\"", back["s"]);
            Assert.AreEqual(false, ((IList<object>)back["l"])[1]);
        }

        [TestCase("{")]
        [TestCase("{\"a\":}")]
        [TestCase("[1,2")]
        [TestCase("{} x")]
        public void RejectsMalformed(string text)
        {
            Assert.Throws<FormatException>(() => Json.Parse(text));
        }
    }

    public class RandomTests
    {
        [Test]
        public void SameSeedSameSequence()
        {
            var a = new DeterministicRandom(7);
            var b = new DeterministicRandom(7);
            for (int i = 0; i < 100; i++) Assert.AreEqual(a.NextULong(), b.NextULong());
        }

        [Test]
        public void RestoredStateContinuesSequence()
        {
            var a = new DeterministicRandom(99);
            a.NextULong();
            var b = new DeterministicRandom(0) { State = a.State };
            Assert.AreEqual(a.NextInt(0, 1000), b.NextInt(0, 1000));
        }

        [Test]
        public void NextIntStaysInRange()
        {
            var r = new DeterministicRandom(3);
            for (int i = 0; i < 10000; i++)
            {
                int v = r.NextInt(-5, 5);
                Assert.That(v, Is.InRange(-5, 4));
            }
        }
    }

    public class ContentTests
    {
        [Test]
        public void ShippedContentLoads()
        {
            ContentDatabase db = TestContent.Load();
            BuildingDef factory = db.GetBuilding("factory");
            Assert.AreEqual(BuildingCategory.Industrial, factory.Category);
            Assert.AreEqual(4, factory.Width);
            Assert.AreEqual(30, factory.Jobs);
            Assert.AreEqual("make_goods", factory.Recipe);
            Assert.IsTrue(db.TryGetResource("steel", out ResourceDef steel));
            Assert.AreEqual(ResourceTier.Intermediate, steel.Tier);
            foreach (BuildingCategory c in Enum.GetValues(typeof(BuildingCategory)))
                Assert.That(db.Buildings, Has.Some.Matches<BuildingDef>(d => d.Category == c), $"no building for {c}");
        }

        [Test]
        public void DuplicateIdsAcrossDocumentsAreRejected()
        {
            const string doc = "{\"buildings\":[{\"id\":\"x\",\"category\":\"Public\"}]}";
            Assert.Throws<FormatException>(() => ContentDatabase.FromJson(doc, doc));
        }

        [Test]
        public void InvalidCategoryIsRejected()
        {
            Assert.Throws<FormatException>(() =>
                ContentDatabase.FromJson("{\"buildings\":[{\"id\":\"x\",\"category\":\"Spaceport\"}]}"));
        }

        [Test]
        public void ModDocumentCanAddBuildings()
        {
            var db = ContentDatabase.FromJson(
                "{\"buildings\":[{\"id\":\"a\",\"category\":\"Public\"}]}",
                "{\"buildings\":[{\"id\":\"b\",\"category\":\"Power\",\"width\":2,\"color\":\"#102030\",\"params\":{\"output\":5}}]}");
            BuildingDef b = db.GetBuilding("b");
            Assert.AreEqual(0x102030, b.Color);
            Assert.AreEqual(5, b.GetParam("output"));
        }
    }
}
