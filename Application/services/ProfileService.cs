using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SnapEye.Services
{
    /// <summary>A single professional experience or education entry.</summary>
    public class ExperienceEntry
    {
        public string EntryId { get; set; } = "";
        public string Company { get; set; } = "";
        public string Role { get; set; } = "";
        public string StartDate { get; set; } = "";
        public string EndDate { get; set; } = "";
        public string Summary { get; set; } = "";
        public string EntryType { get; set; } = "work"; // "work" | "education"
    }

    /// <summary>
    /// HTTP client for the backend Profile / Experience API (<c>/api/profile</c>).
    /// Entries are embedded server-side into the ChromaDB experience collection so
    /// live questions retrieve the most relevant background in real time.
    /// </summary>
    public class ProfileService : IDisposable
    {
        private readonly HttpClient httpClient;

        public ProfileService(string backendUrl)
        {
            httpClient = new HttpClient
            {
                BaseAddress = new Uri(backendUrl),
                Timeout = TimeSpan.FromSeconds(20),
            };
        }

        public void SetAuthToken(string token)
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }

        public async Task<List<ExperienceEntry>> ListAsync()
        {
            var result = new List<ExperienceEntry>();
            var response = await httpClient.GetAsync("/api/profile/experiences").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return result;

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("experiences", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                    result.Add(Parse(el));
            }
            return result;
        }

        public async Task<ExperienceEntry?> AddAsync(ExperienceEntry entry)
        {
            var response = await httpClient.PostAsync(
                "/api/profile/experiences", Serialize(entry)).ConfigureAwait(false);
            return await ReadEntry(response).ConfigureAwait(false);
        }

        public async Task<ExperienceEntry?> UpdateAsync(ExperienceEntry entry)
        {
            var response = await httpClient.PutAsync(
                $"/api/profile/experiences/{Uri.EscapeDataString(entry.EntryId)}",
                Serialize(entry)).ConfigureAwait(false);
            return await ReadEntry(response).ConfigureAwait(false);
        }

        public async Task<bool> DeleteAsync(string entryId)
        {
            if (string.IsNullOrEmpty(entryId))
                return true; // never persisted; nothing to delete
            var response = await httpClient.DeleteAsync(
                $"/api/profile/experiences/{Uri.EscapeDataString(entryId)}").ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }

        private static StringContent Serialize(ExperienceEntry entry)
        {
            var payload = new
            {
                company = entry.Company ?? "",
                role = entry.Role ?? "",
                start_date = entry.StartDate ?? "",
                end_date = entry.EndDate ?? "",
                summary = entry.Summary ?? "",
                entry_type = string.IsNullOrEmpty(entry.EntryType) ? "work" : entry.EntryType,
            };
            return new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }

        private static async Task<ExperienceEntry?> ReadEntry(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
                return null;
            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out _))
                return null;
            return Parse(doc.RootElement);
        }

        private static ExperienceEntry Parse(JsonElement el)
        {
            static string S(JsonElement e, string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? (v.GetString() ?? "") : "";
            return new ExperienceEntry
            {
                EntryId = S(el, "entry_id"),
                Company = S(el, "company"),
                Role = S(el, "role"),
                StartDate = S(el, "start_date"),
                EndDate = S(el, "end_date"),
                Summary = S(el, "summary"),
                EntryType = string.IsNullOrEmpty(S(el, "entry_type")) ? "work" : S(el, "entry_type"),
            };
        }

        public void Dispose() => httpClient?.Dispose();
    }
}
