using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

public class SignedUpload
{
    public string Url;
    public string UploadKey;
}

public partial class APS
{
    // Signed URLs are handed over to Design Automation, so they must stay valid while the work item waits in the queue
    private const int SignedUrlExpirationMinutes = 60;

    private string AppBucketKey => $"{_clientId.ToLowerInvariant()}-exchange-creator";

    // Parses "urn:adsk.objects:os.object:<bucket>/<object>" into its bucket key and object key
    public static (string bucketKey, string objectKey) ParseStorageUrn(string storageUrn)
    {
        var path = storageUrn.Substring(storageUrn.LastIndexOf(':') + 1);
        var separator = path.IndexOf('/');
        return (path.Substring(0, separator), path.Substring(separator + 1));
    }

    public async Task<string> GetSignedDownloadUrl(string bucketKey, string objectKey, string accessToken)
    {
        var result = await GetAsync($"/oss/v2/buckets/{bucketKey}/objects/{Encode(objectKey)}/signeds3download?minutesExpiration={SignedUrlExpirationMinutes}", accessToken);
        return (string)result["url"];
    }

    public async Task<SignedUpload> GetSignedUploadUrl(string bucketKey, string objectKey, string accessToken)
    {
        var result = await GetAsync($"/oss/v2/buckets/{bucketKey}/objects/{Encode(objectKey)}/signeds3upload?minutesExpiration={SignedUrlExpirationMinutes}", accessToken);
        return new SignedUpload { Url = (string)result["urls"][0], UploadKey = (string)result["uploadKey"] };
    }

    public async Task CompleteSignedUpload(string bucketKey, string objectKey, string uploadKey, string accessToken)
    {
        await PostAsync($"/oss/v2/buckets/{bucketKey}/objects/{Encode(objectKey)}/signeds3upload", accessToken, new JsonObject { ["uploadKey"] = uploadKey });
    }

    // Uploads a small file to the app-owned bucket, and returns a signed URL Design Automation can download it from
    public async Task<string> UploadAppObject(string objectKey, string content)
    {
        var accessToken = await GetAppToken();
        await EnsureAppBucket(accessToken);
        var upload = await GetSignedUploadUrl(AppBucketKey, objectKey, accessToken);
        using (var response = await _httpClient.PutAsync(upload.Url, new StringContent(content)))
        {
            response.EnsureSuccessStatusCode();
        }
        await CompleteSignedUpload(AppBucketKey, objectKey, upload.UploadKey, accessToken);
        return await GetSignedDownloadUrl(AppBucketKey, objectKey, accessToken);
    }

    private async Task EnsureAppBucket(string accessToken)
    {
        try
        {
            // Transient bucket: objects are deleted automatically after 24 hours
            await PostAsync("/oss/v2/buckets", accessToken, new JsonObject { ["bucketKey"] = AppBucketKey, ["policyKey"] = "transient" });
        }
        catch (APSException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // The bucket already exists
        }
    }
}
