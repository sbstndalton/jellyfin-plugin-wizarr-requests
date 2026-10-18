using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.WizarrRequests;

public sealed class AccessRequest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Username { get; set; } = "";
    public string Note { get; set; } = "";
    public string Status { get; set; } = "pending";
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset? Decided { get; set; }
    public string DecidedBy { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string AddressHash { get; set; } = "";
    public string InvitationUrl { get; set; } = "";
    public DateTimeOffset? InvitationExpires { get; set; }
    public string ReviewNote { get; set; } = "";
}

public sealed record ProfileInput(string Name, string Email, string Username, string Note, string Website = "");
public sealed record SubmittedRequest(string Id, string Token);
public sealed class RequestError(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed class RequestStore
{
    private readonly object _gate = new();
    private readonly string _file;
    private readonly string _salt;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public RequestStore(string directory)
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _file = Path.Combine(directory, "requests.json");
        var saltFile = Path.Combine(directory, "address-salt");
        if (!File.Exists(saltFile)) { File.WriteAllText(saltFile, Convert.ToHexString(RandomNumberGenerator.GetBytes(32))); Private(saltFile); }
        _salt = File.ReadAllText(saltFile);
        // Never replace malformed data with an empty queue.
        lock (_gate)
        {
            var requests = Load();
            var interrupted = requests.Where(r => r.Status == "approving").ToList();
            foreach (var request in interrupted)
            {
                request.Status = "review-required";
                request.ReviewNote = "Jellyfin restarted during approval. Inspect Wizarr for an invitation before taking further action; no automatic retry was attempted.";
            }
            if (interrupted.Count > 0) Save(requests);
        }
    }

    public SubmittedRequest Submit(ProfileInput input, string address, DateTimeOffset now)
    {
        var name = (input.Name ?? "").Trim();
        var email = (input.Email ?? "").Trim();
        var username = (input.Username ?? "").Trim();
        var note = (input.Note ?? "").Trim();
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl)) throw new RequestError(400, "Enter your name (up to 80 characters).");
        if (email.Length > 254 || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || email.Any(char.IsWhiteSpace)) throw new RequestError(400, "Enter a valid email address.");
        if (!Regex.IsMatch(username, "^[A-Za-z0-9][A-Za-z0-9._-]{2,31}$", RegexOptions.CultureInvariant)) throw new RequestError(400, "Choose a username with 3–32 letters, numbers, dots, underscores or hyphens.");
        if (note.Length is < 1 or > 600 || note.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')) throw new RequestError(400, "Add a short note (up to 600 characters).");
        if (!string.IsNullOrEmpty(input.Website)) throw new RequestError(400, "Unable to submit this request.");
        lock (_gate)
        {
            var requests = Load();
            var addressHash = Hash(_salt + address);
            if (requests.Count >= 5000) throw new RequestError(503, "Requests are temporarily unavailable. Please contact the server owner.");
            if (requests.Count(r => r.AddressHash == addressHash && r.Created > now.AddHours(-24)) >= 5 || requests.Count(r => r.Created > now.AddHours(-1)) >= 100)
                throw new RequestError(429, "Too many requests. Please try again tomorrow or contact the server owner.");
            if (requests.Any(r => r.Email.Equals(email, StringComparison.OrdinalIgnoreCase) && r.Created > now.AddHours(-24)))
                throw new RequestError(429, "A request for this email was recently submitted. Use the private status link, or contact the server owner.");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var request = new AccessRequest { Id = Guid.NewGuid().ToString("N"), Name = name, Email = email, Username = username, Note = note, Created = now, TokenHash = Hash(token), AddressHash = addressHash };
            requests.Add(request);
            Save(requests);
            return new(request.Id, token);
        }
    }

    public IReadOnlyList<AccessRequest> List() { lock (_gate) return Load().OrderByDescending(r => r.Created).ToList(); }
    public AccessRequest Status(string id, string token)
    {
        lock (_gate)
        {
            var request = Load().Find(r => r.Id == id);
            if (token.Length != 64 || request == null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.TokenHash), Encoding.UTF8.GetBytes(Hash(token))))
                throw new RequestError(404, "Request not found. Open the full private status link saved when you submitted.");
            return request;
        }
    }

    public AccessRequest BeginApproval(string id, string admin)
    {
        lock (_gate)
        {
            var requests = Load();
            var request = requests.Find(r => r.Id == id) ?? throw new RequestError(404, "Request not found.");
            if (request.Status != "pending") throw new RequestError(409, "This request has already been reviewed or is being processed. Refresh the queue.");
            request.Status = "approving"; request.DecidedBy = admin;
            Save(requests); // Persist before touching Wizarr. Uncertain remote outcomes are never automatically retried.
            return request;
        }
    }

    public void CompleteApproval(string id, string invitationUrl, DateTimeOffset? expires) => Update(id, r => {
        if (r.Status != "approving") throw new RequestError(409, "Approval state changed; inspect Wizarr before retrying.");
        r.Status = "approved"; r.Decided = DateTimeOffset.UtcNow; r.InvitationUrl = invitationUrl; r.InvitationExpires = expires;
    });
    public void ApprovalUncertain(string id) => Update(id, r => { r.Status = "review-required"; r.ReviewNote = "Wizarr did not return a confirmed result. Inspect its invitations before taking further action; no automatic retry was attempted."; });
    public void Deny(string id, string admin) => Update(id, r => {
        if (r.Status != "pending") throw new RequestError(409, "Only pending requests can be declined.");
        r.Status = "declined"; r.Decided = DateTimeOffset.UtcNow; r.DecidedBy = admin;
    });
    private void Update(string id, Action<AccessRequest> action)
    {
        lock (_gate)
        {
            var requests = Load();
            action(requests.Find(r => r.Id == id) ?? throw new RequestError(404, "Request not found."));
            Save(requests);
        }
    }
    private List<AccessRequest> Load() => File.Exists(_file) ? JsonSerializer.Deserialize<List<AccessRequest>>(File.ReadAllText(_file), Json) ?? throw new InvalidDataException("Request queue is invalid.") : [];
    private void Save(List<AccessRequest> requests)
    {
        var temp = _file + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Private(temp);
            JsonSerializer.Serialize(stream, requests, Json);
            stream.Flush(true);
        }
        File.Move(temp, _file, true);
    }
    private static void Private(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
