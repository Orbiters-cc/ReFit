using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Orbiters.ReFit.Editor
{
    [Serializable]
    internal sealed class ReFitCommissionPriceRange
    {
        public int minCents;
        public int maxCents;
        public string currency;
    }

    [Serializable]
    internal sealed class ReFitCommissionCreator
    {
        public int id;
        public string username;
        public string avatarUrl;
        public string bannerUrl;
        public string profileUrl;
        public ReFitCommissionPriceRange priceRange;
    }

    [Serializable]
    internal sealed class ReFitCommissionDetails
    {
        public string assetName;
        public string sourceAvatar;
        public string targetAvatar;
        public string blendshape;
        public string mode;
    }

    [Serializable]
    internal sealed class ReFitCommissionHandoffRequest
    {
        public List<int> creatorIds = new List<int>();
        public ReFitCommissionDetails details = new ReFitCommissionDetails();
    }

    internal static class ReFitCommissionClient
    {
        [Serializable] internal sealed class Progress { public bool active; public float percent; public string label; }
        [Serializable] internal sealed class Commission
        {
            public string id;
            public string type;
            public string name;
            public Progress progress;
            public ReFitCommissionCreator acceptedCreator;
        }
        [Serializable] internal sealed class CommissionPage { public Commission[] requests; public string nextCursor; }

        // This key is private process state only. Never persist or log the authentication token.
        internal static string AccountScope => ApiUrl + "|" + MCBIntegrationService.TryGetWizardToken();
        internal static bool HasAccount => !string.IsNullOrEmpty(MCBIntegrationService.TryGetWizardToken());

        internal static string CommissionUrl(string id) =>
            (MCBIntegrationService.TryInvokeStaticString("MCBUtils", "getWebsiteUrl") ??
                (IsDevEnvironment ? "http://localhost:3200" : "https://orbiters.cc")).TrimEnd('/') +
            "/commissions/refit/" + Uri.EscapeDataString(id);

        internal static void FetchActiveCommissions(string cursor, Action<CommissionPage, string> completed)
        {
            string token = MCBIntegrationService.TryGetWizardToken();
            if (string.IsNullOrEmpty(token)) { completed(null, "Sign in through MCB to see your commissions."); return; }
            string endpoint = MCBIntegrationService.TryInvokeStaticString("MCBUtils", "getApiUrl", "commissions") ??
                new Uri(new Uri(ApiUrl + "/"), "../commissions").AbsoluteUri;
            var request = UnityWebRequest.Get(endpoint.TrimEnd('/') + "/mine?scope=active" +
                (string.IsNullOrEmpty(cursor) ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            request.SetRequestHeader("Authorization", "Bearer " + token);
            request.timeout = 20;
            Send(request, response =>
            {
                if (!Succeeded(response)) { completed(null, ReadError(response, "Could not load your commissions.")); return; }
                CommissionPage page;
                try
                {
                    page = JsonUtility.FromJson<CommissionPage>(response.downloadHandler.text);
                    if (page?.requests == null) throw new FormatException("Missing commission list.");
                    foreach (var item in page.requests)
                        if (item?.acceptedCreator != null)
                            item.acceptedCreator.avatarUrl = NormalizeMediaUrl(item.acceptedCreator.avatarUrl);
                }
                catch (Exception e) { completed(null, "Could not read commissions: " + e.Message); return; }
                completed(page, null);
            });
        }

        [Serializable]
        private sealed class CreatorListResponse
        {
            public ReFitCommissionCreator[] creators;
        }

        [Serializable]
        private sealed class HandoffResponse
        {
            public string handoffUrl;
            public string expiresAt;
        }

        [Serializable]
        private sealed class ErrorResponse
        {
            public string error;
        }

        private const string DevEnvironmentPrefKey = "ReFit_DevEnvironment";
        internal const string ProductionApiUrl = "https://api.orbiters.cc/refit";
        internal const string DevelopmentApiUrl = "http://localhost:4100/refit";
        private static readonly Dictionary<string, Texture2D> TextureCache = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, Texture2D> CircularTextureCache = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, List<Action<Texture2D>>> TextureWaiters =
            new Dictionary<string, List<Action<Texture2D>>>();

        internal static bool IsDevEnvironment
        {
            get
            {
                bool? mcbValue = MCBIntegrationService.TryGetStaticBool("MCBUtils", "isDevEnvironment");
                return mcbValue ?? EditorPrefs.GetBool(DevEnvironmentPrefKey, false);
            }
            set
            {
                EditorPrefs.SetBool(DevEnvironmentPrefKey, value);
                MCBIntegrationService.TrySetStaticBool("MCBUtils", "isDevEnvironment", value);
            }
        }

        internal static void FetchCreators(Action<ReFitCommissionCreator[], string> completed)
        {
            var request = UnityWebRequest.Get(ApiUrl + "/creators");
            Send(request, response =>
            {
                if (!Succeeded(response))
                {
                    completed?.Invoke(Array.Empty<ReFitCommissionCreator>(), ReadError(response, "Could not load ReFit creators."));
                    return;
                }

                try
                {
                    var payload = JsonUtility.FromJson<CreatorListResponse>(response.downloadHandler.text);
                    var creators = payload != null && payload.creators != null
                        ? payload.creators
                        : Array.Empty<ReFitCommissionCreator>();
                    for (int i = 0; i < creators.Length; i++)
                    {
                        creators[i].avatarUrl = NormalizeMediaUrl(creators[i].avatarUrl);
                        creators[i].bannerUrl = NormalizeMediaUrl(creators[i].bannerUrl);
                    }
                    completed?.Invoke(creators, null);
                }
                catch (Exception exception)
                {
                    completed?.Invoke(Array.Empty<ReFitCommissionCreator>(), $"Could not read creator data: {exception.Message}");
                }
            });
        }

        internal static void CreateHandoff(
            ReFitCommissionHandoffRequest payload,
            Action<string, string> completed,
            List<ReFitCommissionPhoto> photos = null)
        {
            string json = JsonUtility.ToJson(payload);
            var form = new List<IMultipartFormSection> { new MultipartFormDataSection("payload", json) };
            if (photos != null)
                foreach (var photo in photos) form.Add(new MultipartFormFileSection("previews", photo.bytes, photo.name, "image/jpeg"));
            var request = UnityWebRequest.Post(ApiUrl + "/handoffs", form);
            request.timeout = 90;
            string token = MCBIntegrationService.TryGetWizardToken();
            if (!string.IsNullOrEmpty(token)) request.SetRequestHeader("Authorization", "Bearer " + token);

            Send(request, response =>
            {
                if (!Succeeded(response))
                {
                    completed?.Invoke(null, ReadError(response, "Could not open the commission page."));
                    return;
                }

                try
                {
                    var handoff = JsonUtility.FromJson<HandoffResponse>(response.downloadHandler.text);
                    if (handoff == null || string.IsNullOrWhiteSpace(handoff.handoffUrl))
                    {
                        completed?.Invoke(null, "Orbiters returned an invalid commission link.");
                        return;
                    }
                    completed?.Invoke(handoff.handoffUrl, null);
                }
                catch (Exception exception)
                {
                    completed?.Invoke(null, $"Could not read the commission link: {exception.Message}");
                }
            });
        }

        internal static void LoadTexture(string url, Action<Texture2D> completed)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                completed?.Invoke(null);
                return;
            }
            if (TextureCache.TryGetValue(url, out var cached))
            {
                completed?.Invoke(cached);
                return;
            }
            if (TextureWaiters.TryGetValue(url, out var waiting))
            {
                waiting.Add(completed);
                return;
            }

            TextureWaiters[url] = new List<Action<Texture2D>> { completed };
            var request = UnityWebRequestTexture.GetTexture(url);
            Send(request, response =>
            {
                Texture2D texture = null;
                if (Succeeded(response))
                {
                    texture = DownloadHandlerTexture.GetContent(response);
                    TextureCache[url] = texture;
                }
                var callbacks = TextureWaiters[url];
                TextureWaiters.Remove(url);
                foreach (var callback in callbacks) callback?.Invoke(texture);
            });
        }

        internal static void LoadCircularTexture(string url, Action<Texture2D> completed)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                completed?.Invoke(null);
                return;
            }
            if (CircularTextureCache.TryGetValue(url, out var cached))
            {
                completed?.Invoke(cached);
                return;
            }

            LoadTexture(url, texture =>
            {
                if (texture == null)
                {
                    completed?.Invoke(null);
                    return;
                }
                if (!CircularTextureCache.TryGetValue(url, out var circular))
                {
                    circular = CreateCircularAvatarTexture(texture);
                    CircularTextureCache[url] = circular;
                }
                completed?.Invoke(circular);
            });
        }

        internal static Texture2D CreateCircularAvatarTexture(Texture2D texture)
        {
            if (texture == null) return null;

            try
            {
                int size = Mathf.Min(texture.width, texture.height);
                int xOffset = Mathf.Max(0, (texture.width - size) / 2);
                int yOffset = Mathf.Max(0, (texture.height - size) / 2);
                Color[] pixels = texture.GetPixels(xOffset, yOffset, size, size);
                float radius = size * 0.5f;
                float radiusSquared = radius * radius;

                for (int y = 0; y < size; y++)
                {
                    float dy = (y + 0.5f) - radius;
                    for (int x = 0; x < size; x++)
                    {
                        float dx = (x + 0.5f) - radius;
                        if ((dx * dx) + (dy * dy) <= radiusSquared) continue;
                        int index = y * size + x;
                        Color color = pixels[index];
                        color.a = 0f;
                        pixels[index] = color;
                    }
                }

                var circular = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    name = texture.name,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                circular.SetPixels(pixels);
                circular.Apply();
                return circular;
            }
            catch (UnityException)
            {
                return texture;
            }
        }

        internal static string NormalizeMediaUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (Uri.TryCreate(value, UriKind.Absolute, out _)) return value;
            if (!Uri.TryCreate(ApiUrl, UriKind.Absolute, out var api)) return value;
            return api.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/" + value.TrimStart('/');
        }

        internal static string PriceLabel(ReFitCommissionCreator creator)
        {
            if (creator == null || creator.priceRange == null) return "Ask for a quote";
            int min = creator.priceRange.minCents;
            int max = creator.priceRange.maxCents;
            if (min <= 0 && max <= 0) return "Ask for a quote";
            if (min > 0 && max > 0) return $"{min / 100f:0.##} - {max / 100f:0.##} EUR";
            return min > 0 ? $"From {min / 100f:0.##} EUR" : $"Up to {max / 100f:0.##} EUR";
        }

        internal static string FallbackApiUrl(bool isDevEnvironment)
        {
            return isDevEnvironment ? DevelopmentApiUrl : ProductionApiUrl;
        }

        private static string ApiUrl =>
            MCBIntegrationService.TryInvokeStaticString("MCBUtils", "getApiUrl", "refit") ?? FallbackApiUrl(IsDevEnvironment);

        private static void Send(UnityWebRequest request, Action<UnityWebRequest> completed)
        {
            var operation = request.SendWebRequest();
            operation.completed += _ =>
            {
                try { completed?.Invoke(request); }
                finally { request.Dispose(); }
            };
        }

        private static bool Succeeded(UnityWebRequest request)
        {
            return request.result == UnityWebRequest.Result.Success && request.responseCode >= 200 && request.responseCode < 300;
        }

        private static string ReadError(UnityWebRequest request, string fallback)
        {
            string body = request.downloadHandler != null ? request.downloadHandler.text : null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    var parsed = JsonUtility.FromJson<ErrorResponse>(body);
                    if (!string.IsNullOrWhiteSpace(parsed?.error)) return parsed.error;
                }
                catch { }
            }
            return !string.IsNullOrWhiteSpace(request.error) ? request.error : fallback;
        }

    }
}
