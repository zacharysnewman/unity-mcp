using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Helpers;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Animation
{
    internal static class AnimatorRead
    {
        public static object GetInfo(JObject @params)
        {
            var go = ObjectResolver.ResolveGameObject(@params["target"], @params["searchMethod"]?.ToString());
            if (go == null)
                return new { success = false, message = "Target GameObject not found" };

            var animator = go.GetComponent<Animator>();
            if (animator == null)
                return new { success = false, message = $"No Animator component on '{go.name}'" };

            var parameters = new List<object>();
            for (int i = 0; i < animator.parameterCount; i++)
            {
                var p = animator.GetParameter(i);
                parameters.Add(new
                {
                    name = p.name,
                    type = p.type.ToString(),
                    defaultFloat = p.defaultFloat,
                    defaultInt = p.defaultInt,
                    defaultBool = p.defaultBool
                });
            }

            var layers = new List<object>();
            for (int i = 0; i < animator.layerCount; i++)
            {
                var stateInfo = animator.IsInTransition(i)
                    ? animator.GetNextAnimatorStateInfo(i)
                    : animator.GetCurrentAnimatorStateInfo(i);

                layers.Add(new
                {
                    index = i,
                    name = animator.GetLayerName(i),
                    weight = animator.GetLayerWeight(i),
                    currentStateHash = stateInfo.fullPathHash,
                    currentStateNormalizedTime = stateInfo.normalizedTime,
                    currentStateLength = stateInfo.length,
                    isInTransition = animator.IsInTransition(i)
                });
            }

            var allClips = new List<object>();
            if (animator.runtimeAnimatorController != null)
            {
                foreach (var clip in animator.runtimeAnimatorController.animationClips)
                {
                    allClips.Add(new
                    {
                        name = clip.name,
                        length = clip.length,
                        frameRate = clip.frameRate,
                        isLooping = clip.isLooping,
                        wrapMode = clip.wrapMode.ToString()
                    });
                }
            }

            int clipsPageSize = Math.Min(Math.Max(1, @params["pageSize"]?.ToObject<int?>() ?? @params["page_size"]?.ToObject<int?>() ?? 50), 200);
            int clipsCursor = Math.Max(0, @params["cursor"]?.ToObject<int?>() ?? 0);
            int clipsTotal = allClips.Count;
            if (clipsCursor > clipsTotal) clipsCursor = clipsTotal;
            int clipsEnd = Math.Min(clipsTotal, clipsCursor + clipsPageSize);
            var clips = allClips.GetRange(clipsCursor, clipsEnd - clipsCursor);
            bool clipsTruncated = clipsEnd < clipsTotal;

            return new
            {
                success = true,
                data = new
                {
                    gameObject = go.name,
                    enabled = animator.enabled,
                    speed = animator.speed,
                    hasController = animator.runtimeAnimatorController != null,
                    controllerName = animator.runtimeAnimatorController?.name,
                    applyRootMotion = animator.applyRootMotion,
                    updateMode = animator.updateMode.ToString(),
                    cullingMode = animator.cullingMode.ToString(),
                    parameterCount = animator.parameterCount,
                    layerCount = animator.layerCount,
                    parameters,
                    layers,
                    clips,
                    clipsTotal,
                    clipsCursor,
                    clipsPageSize,
                    clipsNextCursor = clipsTruncated ? clipsEnd.ToString() : null,
                    clipsTruncated,
                }
            };
        }

        public static object GetParameter(JObject @params)
        {
            var go = ObjectResolver.ResolveGameObject(@params["target"], @params["searchMethod"]?.ToString());
            if (go == null)
                return new { success = false, message = "Target GameObject not found" };

            var animator = go.GetComponent<Animator>();
            if (animator == null)
                return new { success = false, message = $"No Animator component on '{go.name}'" };

            string paramName = @params["parameterName"]?.ToString();
            if (string.IsNullOrEmpty(paramName))
                return new { success = false, message = "'parameterName' is required" };

            AnimatorControllerParameter found = null;
            for (int i = 0; i < animator.parameterCount; i++)
            {
                var p = animator.GetParameter(i);
                if (p.name == paramName)
                {
                    found = p;
                    break;
                }
            }

            if (found == null)
                return new { success = false, message = $"Parameter '{paramName}' not found on Animator" };

            object value;
            switch (found.type)
            {
                case AnimatorControllerParameterType.Float:
                    value = animator.GetFloat(paramName);
                    break;
                case AnimatorControllerParameterType.Int:
                    value = animator.GetInteger(paramName);
                    break;
                case AnimatorControllerParameterType.Bool:
                    value = animator.GetBool(paramName);
                    break;
                case AnimatorControllerParameterType.Trigger:
                    value = animator.GetBool(paramName);
                    break;
                default:
                    value = null;
                    break;
            }

            return new
            {
                success = true,
                data = new
                {
                    name = found.name,
                    type = found.type.ToString(),
                    value
                }
            };
        }
    }
}
