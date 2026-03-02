using System;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using MCPForUnity.Editor.Tools;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class ReadConsoleTests
    {
        [Test]
        public void HandleCommand_Clear_Works()
        {
            Debug.Log("Log to clear");

            var getBefore = ToJObject(ReadConsole.HandleCommand(new JObject
            {
                ["action"] = "get",
                ["includeErrors"] = true,
                ["includeWarnings"] = true,
                ["includeLogs"] = true,
            }));
            Assert.IsTrue(getBefore.Value<bool>("success"), getBefore.ToString());
            var itemsBefore = getBefore["data"]?["items"] as JArray;
            Assert.IsTrue(itemsBefore != null && itemsBefore.Count > 0, "Setup failed: console should have logs.");

            var result = ToJObject(ReadConsole.HandleCommand(new JObject { ["action"] = "clear" }));
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());

            var getAfter = ToJObject(ReadConsole.HandleCommand(new JObject
            {
                ["action"] = "get",
                ["includeErrors"] = true,
                ["includeWarnings"] = true,
                ["includeLogs"] = true,
            }));
            Assert.IsTrue(getAfter.Value<bool>("success"), getAfter.ToString());
            var itemsAfter = getAfter["data"]?["items"] as JArray;
            Assert.IsTrue(itemsAfter == null || itemsAfter.Count == 0, "Console should be empty after clear.");
        }

        [Test]
        public void HandleCommand_Get_ReturnsStructuredEntries()
        {
            string uniqueMessage = $"Test Log Message {Guid.NewGuid()}";
            Debug.Log(uniqueMessage);

            var result = ToJObject(ReadConsole.HandleCommand(new JObject
            {
                ["action"] = "get",
                ["includeErrors"] = true,
                ["includeWarnings"] = true,
                ["includeLogs"] = true,
                ["pageSize"] = 500,
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            var items = result["data"]?["items"] as JArray;
            Assert.IsNotNull(items, "items array should not be null.");
            Assert.IsTrue(items.Count > 0, "Should retrieve at least one entry.");

            bool found = false;
            foreach (var entry in items)
            {
                if (entry["message"]?.ToString().Contains(uniqueMessage) == true)
                {
                    found = true;
                    Assert.IsNotNull(entry["id"], "Each entry should have an id.");
                    Assert.IsNotNull(entry["type"], "Each entry should have a type.");
                    Assert.IsTrue(entry["occurrenceCount"]?.Value<int>() >= 1, "occurrenceCount should be >= 1.");
                    break;
                }
            }
            Assert.IsTrue(found, $"The unique message '{uniqueMessage}' was not found.");
        }

        [Test]
        public void HandleCommand_Get_DefaultsToErrorsOnly()
        {
            ReadConsole.HandleCommand(new JObject { ["action"] = "clear" });
            Debug.Log("This is a plain log");
            Debug.LogWarning("This is a warning");
            Debug.LogError("This is an error");

            var result = ToJObject(ReadConsole.HandleCommand(new JObject { ["action"] = "get" }));
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            var items = result["data"]?["items"] as JArray;
            Assert.IsNotNull(items);

            foreach (var entry in items)
            {
                var type = entry["type"]?.ToString().ToLower();
                Assert.IsTrue(
                    type == "error" || type == "exception" || type == "assert",
                    $"Unexpected type '{type}' in default (errors-only) result.");
            }
        }

        [Test]
        public void HandleCommand_Get_DeduplicatesDuplicateMessages()
        {
            ReadConsole.HandleCommand(new JObject { ["action"] = "clear" });
            string duplicateMsg = $"Repeated error {Guid.NewGuid()}";
            Debug.LogError(duplicateMsg);
            Debug.LogError(duplicateMsg);
            Debug.LogError(duplicateMsg);

            var result = ToJObject(ReadConsole.HandleCommand(new JObject
            {
                ["action"] = "get",
                ["includeErrors"] = true,
                ["pageSize"] = 500,
            }));
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            var items = result["data"]?["items"] as JArray;
            Assert.IsNotNull(items);

            bool found = false;
            foreach (var entry in items)
            {
                if (entry["message"]?.ToString().Contains(duplicateMsg) == true)
                {
                    found = true;
                    Assert.AreEqual(3, entry["occurrenceCount"]?.Value<int>(),
                        "Duplicate messages should be counted.");
                    break;
                }
            }
            Assert.IsTrue(found, "The repeated error message should appear in results.");
        }

        [Test]
        public void HandleCommand_Get_Paginated()
        {
            ReadConsole.HandleCommand(new JObject { ["action"] = "clear" });
            for (int i = 0; i < 5; i++)
                Debug.LogError($"Paginated error {i} {Guid.NewGuid()}");

            var result = ToJObject(ReadConsole.HandleCommand(new JObject
            {
                ["action"] = "get",
                ["includeErrors"] = true,
                ["pageSize"] = 2,
                ["cursor"] = 0,
            }));
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            var data = result["data"] as JObject;
            Assert.IsNotNull(data);
            var items = data["items"] as JArray;
            Assert.IsNotNull(items);
            Assert.AreEqual(2, items.Count, "page_size=2 should return 2 items.");
            Assert.IsNotNull(data["nextCursor"], "Should have a nextCursor when more pages exist.");
            Assert.IsTrue(data["hasMore"]?.Value<bool>() == true, "hasMore should be true.");
        }

        [Test]
        public void GetFirstErrors_ReturnsEarliestErrors()
        {
            ReadConsole.HandleCommand(new JObject { ["action"] = "clear" });
            Debug.LogError($"First {Guid.NewGuid()}");
            Debug.LogError($"Second {Guid.NewGuid()}");
            Debug.LogError($"Third {Guid.NewGuid()}");

            var errors = ReadConsole.GetFirstErrors(10);
            Assert.AreEqual(3, errors.Count);
            foreach (var e in errors)
            {
                var entry = JObject.FromObject(e);
                Assert.IsNotNull(entry["id"]);
                Assert.IsNotNull(entry["type"]);
                Assert.IsNotNull(entry["message"]);
                Assert.IsTrue(entry["occurrenceCount"]?.Value<int>() >= 1);
            }
        }

        [Test]
        public void GetFirstErrors_CapsAtMaxCount()
        {
            ReadConsole.HandleCommand(new JObject { ["action"] = "clear" });
            for (int i = 0; i < 5; i++)
                Debug.LogError($"Error {i} {Guid.NewGuid()}");

            var errors = ReadConsole.GetFirstErrors(3);
            Assert.AreEqual(3, errors.Count);
        }

        [Test]
        public void GetFirstErrors_DeduplicatesAndCountsOccurrences()
        {
            ReadConsole.HandleCommand(new JObject { ["action"] = "clear" });
            string msg = $"Repeated {Guid.NewGuid()}";
            Debug.LogError(msg);
            Debug.LogError(msg);
            Debug.LogError(msg);

            var errors = ReadConsole.GetFirstErrors(10);
            Assert.AreEqual(1, errors.Count);
            var entry = JObject.FromObject(errors[0]);
            Assert.AreEqual(3, entry["occurrenceCount"]?.Value<int>());
        }

        [Test]
        public void GetFirstErrors_ExcludesWarningsAndLogs()
        {
            ReadConsole.HandleCommand(new JObject { ["action"] = "clear" });
            Debug.Log($"Log {Guid.NewGuid()}");
            Debug.LogWarning($"Warning {Guid.NewGuid()}");
            Debug.LogError($"Error {Guid.NewGuid()}");

            var errors = ReadConsole.GetFirstErrors(10);
            Assert.AreEqual(1, errors.Count);
            var entry = JObject.FromObject(errors[0]);
            Assert.AreEqual("Error", entry["type"]?.ToString());
        }

        [Test]
        public void HandleCommand_Get_EntryHasStableId()
        {
            ReadConsole.HandleCommand(new JObject { ["action"] = "clear" });
            string msg = $"Stable ID error {Guid.NewGuid()}";
            Debug.LogError(msg);

            var result1 = ToJObject(ReadConsole.HandleCommand(new JObject
            {
                ["action"] = "get",
                ["includeErrors"] = true,
                ["pageSize"] = 500,
            }));
            var result2 = ToJObject(ReadConsole.HandleCommand(new JObject
            {
                ["action"] = "get",
                ["includeErrors"] = true,
                ["pageSize"] = 500,
            }));

            var items1 = result1["data"]?["items"] as JArray;
            var items2 = result2["data"]?["items"] as JArray;
            Assert.IsNotNull(items1);
            Assert.IsNotNull(items2);

            string id1 = null, id2 = null;
            foreach (var entry in items1)
                if (entry["message"]?.ToString().Contains(msg) == true) { id1 = entry["id"]?.ToString(); break; }
            foreach (var entry in items2)
                if (entry["message"]?.ToString().Contains(msg) == true) { id2 = entry["id"]?.ToString(); break; }

            Assert.IsNotNull(id1, "Entry should have an id in first call.");
            Assert.AreEqual(id1, id2, "Same message should produce the same id across calls.");
        }
    }
}
