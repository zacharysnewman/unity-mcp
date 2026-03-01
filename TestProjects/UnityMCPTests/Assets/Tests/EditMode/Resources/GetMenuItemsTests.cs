using NUnit.Framework;
using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Resources.MenuItems;
using System;
using System.Linq;

namespace MCPForUnityTests.Editor.Resources.MenuItems
{
    public class GetMenuItemsTests
    {
        private static JObject ToJO(object o) => JObject.FromObject(o);

        private static JArray GetItems(JObject jo) => (JArray)jo["data"]["items"];
        private static int GetTotal(JObject jo) => jo["data"].Value<int>("total");
        private static bool GetTruncated(JObject jo) => jo["data"].Value<bool>("truncated");

        [Test]
        public void NoSearch_ReturnsSuccessAndPagedShape()
        {
            var res = GetMenuItems.HandleCommand(new JObject { ["search"] = "", ["refresh"] = false });
            var jo = ToJO(res);
            Assert.IsTrue((bool)jo["success"], "Expected success true");
            Assert.IsNotNull(jo["data"], "Expected data field present");
            Assert.AreEqual(JTokenType.Object, jo["data"].Type, "Expected data to be an object");
            Assert.IsNotNull(jo["data"]["items"], "Expected data.items present");
            Assert.AreEqual(JTokenType.Array, jo["data"]["items"].Type, "Expected data.items to be an array");

            // Validate list is sorted ascending when there are multiple items
            var arr = GetItems(jo);
            if (arr.Count >= 2)
            {
                var original = arr.Select(t => (string)t).ToList();
                var sorted = original.OrderBy(s => s, StringComparer.Ordinal).ToList();
                CollectionAssert.AreEqual(sorted, original, "Expected menu items to be sorted ascending");
            }
        }

        [Test]
        public void SearchNoMatch_ReturnsEmpty()
        {
            var res = GetMenuItems.HandleCommand(new JObject { ["search"] = "___unlikely___term___" });
            var jo = ToJO(res);
            Assert.IsTrue((bool)jo["success"], "Expected success true");
            Assert.AreEqual(JTokenType.Object, jo["data"].Type, "Expected data to be an object");
            Assert.AreEqual(0, GetItems(jo).Count, "Expected no results for unlikely search term");
            Assert.AreEqual(0, GetTotal(jo), "Expected total=0 for no matches");
        }

        [Test]
        public void SearchMatchesExistingItem_ReturnsContainingItem()
        {
            // Get the full list first (large page to avoid truncation)
            var listRes = GetMenuItems.HandleCommand(new JObject { ["search"] = "", ["refresh"] = false, ["pageSize"] = 1000 });
            var listJo = ToJO(listRes);
            var arr = GetItems(listJo);
            if (arr.Count > 0)
            {
                var first = (string)arr[0];
                var term = first.Length > 4 ? first.Substring(1, Math.Min(3, first.Length - 2)) : first;
                term = term.ToLowerInvariant();

                var res = GetMenuItems.HandleCommand(new JObject { ["search"] = term, ["refresh"] = false, ["pageSize"] = 1000 });
                var jo = ToJO(res);
                Assert.IsTrue((bool)jo["success"], "Expected success true");
                var names = GetItems(jo).Select(t => (string)t).ToList();
                CollectionAssert.Contains(names, first, "Expected search results to include the sampled item");
            }
            else
            {
                Assert.Pass("No menu items available to perform a content-based search assertion.");
            }
        }

        [Test]
        public void Pagination_SmallPageSize_TruncatesAndReturnsNextCursor()
        {
            // Get the total without page limit
            var allRes = GetMenuItems.HandleCommand(new JObject { ["search"] = "", ["pageSize"] = 1000 });
            var allJo = ToJO(allRes);
            int total = GetTotal(allJo);

            if (total < 2)
            {
                Assert.Pass("Not enough menu items to test pagination.");
                return;
            }

            // Request page of 1
            var res = GetMenuItems.HandleCommand(new JObject { ["search"] = "", ["pageSize"] = 1 });
            var jo = ToJO(res);
            Assert.IsTrue((bool)jo["success"]);
            Assert.AreEqual(1, GetItems(jo).Count, "Expected exactly 1 item with pageSize=1");
            Assert.IsTrue(GetTruncated(jo), "Expected truncated=true");
            Assert.IsNotNull(jo["data"]["next_cursor"], "Expected next_cursor when truncated");
        }

        [Test]
        public void Pagination_CursorAdvances_ReturnsNextSlice()
        {
            var allRes = GetMenuItems.HandleCommand(new JObject { ["search"] = "", ["pageSize"] = 1000 });
            int total = GetTotal(ToJO(allRes));

            if (total < 3)
            {
                Assert.Pass("Not enough menu items to test cursor advance.");
                return;
            }

            var p1 = ToJO(GetMenuItems.HandleCommand(new JObject { ["pageSize"] = 2, ["cursor"] = 0 }));
            var p2 = ToJO(GetMenuItems.HandleCommand(new JObject { ["pageSize"] = 2, ["cursor"] = 2 }));

            var items1 = GetItems(p1).Select(t => (string)t).ToList();
            var items2 = GetItems(p2).Select(t => (string)t).ToList();

            CollectionAssert.AreNotEqual(items1, items2, "Cursor-advanced page should return different items");
        }
    }
}
