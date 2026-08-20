using NUnit.Framework;

namespace Uslog.PackageManager.Editor.Tests
{
    public class JsonTests
    {
        [Test]
        public void 入れ子のオブジェクトと配列を読める()
        {
            var json = JsonValue.Parse(@"{""a"":{""b"":[1,2,{""c"":""d""}]}}");

            Assert.AreEqual(3, json["a"]["b"].Count);
            Assert.AreEqual("d", json["a"]["b"][2]["c"].AsString);
        }

        [Test]
        public void 無いキーを引いても落ちない()
        {
            // 呼び出し側に null チェックを書かせないための約束。
            var json = JsonValue.Parse("{}");

            Assert.IsTrue(json["nope"]["deeper"]["deeper"].IsNull);
            Assert.IsNull(json["nope"].AsString);
        }

        [Test]
        public void キーの順序を保つ()
        {
            // manifest.json を書き戻すので、並びが変わると毎回差分が出る。
            var json = JsonValue.Parse(@"{""z"":1,""a"":2,""m"":3}");

            CollectionAssert.AreEqual(new[] { "z", "a", "m" }, json.Keys);
        }

        [Test]
        public void 整数を小数に化けさせない()
        {
            var json = JsonValue.Parse(@"{""n"":1,""f"":1.5,""e"":1e3}");

            StringAssert.Contains("\"n\": 1,", json.ToJson());
            StringAssert.Contains("\"f\": 1.5", json.ToJson());
            StringAssert.Contains("\"e\": 1e3", json.ToJson());
        }

        [Test]
        public void エスケープを往復できる()
        {
            var original = "改行\nタブ\t引用\"円\\スラッシュ/";
            var json = JsonValue.NewObject().Set("v", original);

            Assert.AreEqual(original, JsonValue.Parse(json.ToJson())["v"].AsString);
        }

        [Test]
        public void ユニコードエスケープを読める()
        {
            Assert.AreEqual("あ", JsonValue.Parse(@"{""v"":""あ""}")["v"].AsString);
        }

        [Test]
        public void 壊れた入力は例外にする()
        {
            Assert.Throws<JsonException>(() => JsonValue.Parse("{"));
            Assert.Throws<JsonException>(() => JsonValue.Parse("{\"a\":}"));
            Assert.Throws<JsonException>(() => JsonValue.Parse("[1,2"));
            Assert.Throws<JsonException>(() => JsonValue.Parse("{} extra"));
        }

        [Test]
        public void TryParse_は投げずに_false_を返す()
        {
            Assert.IsFalse(JsonValue.TryParse("nope", out _));
            Assert.IsTrue(JsonValue.TryParse("{}", out var ok));
            Assert.IsTrue(ok.IsObject);
        }

        [Test]
        public void 深すぎる入れ子で落ちない()
        {
            // StackOverflow はプロセスごと落ちる。例外にして受け止められるようにする。
            var deep = new string('[', 500) + new string(']', 500);

            Assert.Throws<JsonException>(() => JsonValue.Parse(deep));
        }

        [Test]
        public void 同じキーを_Set_しても重複しない()
        {
            var json = JsonValue.NewObject().Set("a", "1").Set("a", "2");

            Assert.AreEqual(1, json.Count);
            Assert.AreEqual("2", json["a"].AsString);
        }

        [Test]
        public void Remove_でキーの並びからも消える()
        {
            var json = JsonValue.Parse(@"{""a"":1,""b"":2}");

            Assert.IsTrue(json.Remove("a"));
            CollectionAssert.AreEqual(new[] { "b" }, json.Keys);
            Assert.IsFalse(json.Remove("a"));
        }

        [Test]
        public void 空のオブジェクトと配列は_1_行で書く()
        {
            var json = JsonValue.NewObject()
                .Set("o", JsonValue.NewObject())
                .Set("a", JsonValue.NewArray());

            StringAssert.Contains("\"o\": {}", json.ToJson());
            StringAssert.Contains("\"a\": []", json.ToJson());
        }
    }
}
