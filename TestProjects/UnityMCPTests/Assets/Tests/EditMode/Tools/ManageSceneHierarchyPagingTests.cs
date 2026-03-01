using NUnit.Framework;
using UnityEngine;
using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Tools;

namespace MCPForUnityTests.Editor.Tools
{
    public class ManageSceneHierarchyPagingTests
    {
        private GameObject _root;
        private readonly System.Collections.Generic.List<GameObject> _created = new System.Collections.Generic.List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _created.Count; i++)
            {
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            }
            _created.Clear();

            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        [Test]
        public void GetHierarchy_NodeSummary_ContainsNameInstanceIDChildCount()
        {
            var go = new GameObject("HS_SummaryTest");
            go.AddComponent<UnityEngine.BoxCollider>();
            _created.Add(go);
            var child = new GameObject("HS_SummaryChild");
            child.transform.SetParent(go.transform);

            var p = new JObject
            {
                ["action"] = "get_hierarchy",
                ["pageSize"] = 100,
            };
            var raw = ManageScene.HandleCommand(p);
            var res = raw as JObject ?? JObject.FromObject(raw);
            Assert.IsTrue(res.Value<bool>("success"), res.ToString());

            var items = res["data"]["items"] as JArray;
            Assert.IsNotNull(items);

            JObject node = null;
            foreach (JObject item in items)
            {
                if (item.Value<string>("name") == "HS_SummaryTest")
                {
                    node = item;
                    break;
                }
            }
            Assert.IsNotNull(node, "Expected to find HS_SummaryTest in hierarchy items.");
            Assert.IsNotNull(node["instanceID"], "Expected instanceID field.");
            Assert.AreEqual(1, node.Value<int>("childCount"), "Expected childCount=1.");
            // Slimmed-down payload must NOT include legacy fields
            Assert.IsNull(node["activeSelf"], "activeSelf should not be in slim payload.");
            Assert.IsNull(node["tag"], "tag should not be in slim payload.");
            Assert.IsNull(node["layer"], "layer should not be in slim payload.");
            Assert.IsNull(node["isStatic"], "isStatic should not be in slim payload.");
            Assert.IsNull(node["childrenTruncated"], "childrenTruncated should not be in slim payload.");
            Assert.IsNull(node["childrenCursor"], "childrenCursor should not be in slim payload.");
        }

        [Test]
        public void GetHierarchy_FilterByTag_ReturnsOnlyMatchingNodes()
        {
            var tagged = new GameObject("HS_Tagged");
            tagged.tag = "Respawn";
            _created.Add(tagged);

            var untagged = new GameObject("HS_Untagged");
            _created.Add(untagged);

            var p = new JObject
            {
                ["action"] = "get_hierarchy",
                ["tag"] = "Respawn",
                ["pageSize"] = 100,
            };
            var raw = ManageScene.HandleCommand(p);
            var res = raw as JObject ?? JObject.FromObject(raw);
            Assert.IsTrue(res.Value<bool>("success"), res.ToString());

            var items = res["data"]["items"] as JArray;
            Assert.IsNotNull(items);
            foreach (JObject item in items)
            {
                Assert.AreNotEqual("HS_Untagged", item.Value<string>("name"),
                    "Untagged object should not appear when filtering by tag.");
            }
            Assert.IsTrue(items.Any(i => i.Value<string>("name") == "HS_Tagged"),
                "Tagged object must appear in filtered results.");
        }

        [Test]
        public void GetHierarchy_FilterByLayer_ReturnsOnlyMatchingNodes()
        {
            int testLayer = 8; // "PostProcessing" or any layer index present in the project
            var onLayer = new GameObject("HS_OnLayer8");
            onLayer.layer = testLayer;
            _created.Add(onLayer);

            var otherLayer = new GameObject("HS_OnLayer0");
            otherLayer.layer = 0;
            _created.Add(otherLayer);

            var p = new JObject
            {
                ["action"] = "get_hierarchy",
                ["layer"] = testLayer,
                ["pageSize"] = 100,
            };
            var raw = ManageScene.HandleCommand(p);
            var res = raw as JObject ?? JObject.FromObject(raw);
            Assert.IsTrue(res.Value<bool>("success"), res.ToString());

            var items = res["data"]["items"] as JArray;
            Assert.IsNotNull(items);
            foreach (JObject item in items)
            {
                Assert.AreNotEqual("HS_OnLayer0", item.Value<string>("name"),
                    "Layer 0 object should not appear when filtering by layer 8.");
            }
            Assert.IsTrue(items.Any(i => i.Value<string>("name") == "HS_OnLayer8"),
                "Layer 8 object must appear in filtered results.");
        }

        [Test]
        public void GetHierarchy_FilterActiveOnly_ExcludesInactiveNodes()
        {
            var active = new GameObject("HS_Active");
            _created.Add(active);

            var inactive = new GameObject("HS_Inactive");
            inactive.SetActive(false);
            _created.Add(inactive);

            var p = new JObject
            {
                ["action"] = "get_hierarchy",
                ["activeOnly"] = true,
                ["pageSize"] = 100,
            };
            var raw = ManageScene.HandleCommand(p);
            var res = raw as JObject ?? JObject.FromObject(raw);
            Assert.IsTrue(res.Value<bool>("success"), res.ToString());

            var items = res["data"]["items"] as JArray;
            Assert.IsNotNull(items);
            foreach (JObject item in items)
            {
                Assert.AreNotEqual("HS_Inactive", item.Value<string>("name"),
                    "Inactive object should not appear when active_only=true.");
            }
        }

        [Test]
        public void GetHierarchy_PaginatesRoots_AndSupportsChildrenPaging()
        {
            // Arrange: create many roots so paging must occur.
            // Note: Keep counts modest to avoid slowing EditMode tests.
            const int rootCount = 40;
            for (int i = 0; i < rootCount; i++)
            {
                _created.Add(new GameObject($"HS_Root_{i:D2}"));
            }

            _root = new GameObject("HS_Parent");
            for (int i = 0; i < 15; i++)
            {
                var child = new GameObject($"HS_Child_{i:D2}");
                child.transform.SetParent(_root.transform);
            }

            // Act: request a small page to force truncation
            var p1 = new JObject
            {
                ["action"] = "get_hierarchy",
                ["pageSize"] = 10,
            };
            var raw1 = ManageScene.HandleCommand(p1);
            var res1 = raw1 as JObject ?? JObject.FromObject(raw1);

            // Assert: envelope success + payload shape
            Assert.IsTrue(res1.Value<bool>("success"), res1.ToString());
            var data1 = res1["data"] as JObject;
            Assert.IsNotNull(data1, "Expected data payload to be an object.");
            Assert.AreEqual("roots", data1.Value<string>("scope"));
            Assert.AreEqual(true, data1.Value<bool>("truncated"), "Expected truncation when pageSize < root count.");
            Assert.IsNotNull(data1["next_cursor"], "Expected next_cursor when truncated.");

            var items1 = data1["items"] as JArray;
            Assert.IsNotNull(items1, "Expected items array.");
            Assert.AreEqual(10, items1.Count, "Expected exactly pageSize items returned.");

            // Act: fetch next page of roots using next_cursor
            var cursor = data1.Value<string>("next_cursor");
            var p2 = new JObject
            {
                ["action"] = "get_hierarchy",
                ["pageSize"] = 10,
                ["cursor"] = cursor,
            };
            var raw2 = ManageScene.HandleCommand(p2);
            var res2 = raw2 as JObject ?? JObject.FromObject(raw2);
            Assert.IsTrue(res2.Value<bool>("success"), res2.ToString());
            var data2 = res2["data"] as JObject;
            Assert.IsNotNull(data2);
            var items2 = data2["items"] as JArray;
            Assert.IsNotNull(items2);
            Assert.AreEqual(10, items2.Count);

            // Act: page children of a specific parent via 'parent' param (instance ID)
            var pChildren = new JObject
            {
                ["action"] = "get_hierarchy",
                ["parent"] = _root.GetInstanceID(),
                ["pageSize"] = 7,
            };
            var rawChildren = ManageScene.HandleCommand(pChildren);
            var resChildren = rawChildren as JObject ?? JObject.FromObject(rawChildren);
            Assert.IsTrue(resChildren.Value<bool>("success"), resChildren.ToString());
            var dataChildren = resChildren["data"] as JObject;
            Assert.IsNotNull(dataChildren);
            Assert.AreEqual("children", dataChildren.Value<string>("scope"));
            Assert.AreEqual(true, dataChildren.Value<bool>("truncated"));
            Assert.IsNotNull(dataChildren["next_cursor"]);
            var childItems = dataChildren["items"] as JArray;
            Assert.IsNotNull(childItems);
            Assert.AreEqual(7, childItems.Count);
        }
    }
}
