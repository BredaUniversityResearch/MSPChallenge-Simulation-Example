using System.Collections.Specialized;
using System.Net.Http.Headers;
using MSPChallenge_Simulation_Example.Api;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace MSPChallenge_Simulation_Example.Communication;

public class MspClient
{
    private readonly HttpClient m_client;
    private Action<Exception>? m_defaultErrorHandler;

    public class ApiResponseWrapper
    {
        // ReSharper disable once InconsistentNaming
        public bool success = false;
        // ReSharper disable once InconsistentNaming
        public string message = null;
        // ReSharper disable once InconsistentNaming
        public JToken payload = null;
    }

    public MspClient(string apiBaseUrl, ApiToken apiAccessToken, ApiToken apiRefreshToken)
    {
        // todo: implement token refresh
        m_client = new HttpClient
        {
            BaseAddress = new Uri(apiBaseUrl)
        };
        m_client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiAccessToken.Token);
    }
    
    public void SetDefaultHttpPostErrorHandler(Action<Exception>? onError)
    {
        m_defaultErrorHandler = onError;
    }
    
    public void HttpPost(string uri, NameValueCollection postValues,
        Action? onSuccess = null,
        Action<Exception>? onError = null)
    {
        HttpPostInternal(uri, postValues, (ApiResponseWrapper wrapper) =>
        {
            if (wrapper.success)
            {
                onSuccess?.Invoke();
            }
            else
            {
                onError?.Invoke(new Exception(wrapper.message ?? "Unknown error"));
            }
        }, onError);
    }    

    public void HttpPost<TTargetType>(string uri, NameValueCollection postValues,
        Action<TTargetType>? onSuccess = null,
        Action<Exception>? onError = null)
    {
        HttpPostInternal(uri, postValues, (ApiResponseWrapper wrapper) =>
        {
            var result = wrapper.payload.ToObject<TTargetType>();
            if (result == null)
            {
                throw new Exception($"Post request for {uri} failed: JSON Decode Failed");
            }
            if (wrapper.success)
            {
                onSuccess?.Invoke(result);
            }
            else
            {
                onError?.Invoke(new Exception(wrapper.message ?? "Unknown error"));
            }
        }, onError);
    }

    private void HttpPostInternal(
        string uri,
        NameValueCollection postValues,
        Action<ApiResponseWrapper>? onSuccess = null,
        Action<Exception>? onError = null
    )  {
        onError ??= m_defaultErrorHandler;
        var content = new FormUrlEncodedContent(postValues.AllKeys
            .Where(k => k != null)
            .ToDictionary(k => k!, k => postValues[k]!));
        var requestUri = m_client.BaseAddress != null ? new Uri(m_client.BaseAddress, uri.TrimStart('/')) : new Uri(uri);
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = content
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));        
        m_client.SendAsync(request).ContinueWith((Task<HttpResponseMessage> postTask) =>
        {
            if (postTask is { IsCompletedSuccessfully: false, Exception: not null })
            {
                onError?.Invoke(postTask.Exception);
                return;
            }
            var response = postTask.Result;
            response.Content.ReadAsStringAsync().ContinueWith((Task<string> readTask) =>
            {
                if (readTask is { IsCompletedSuccessfully: false, Exception: not null })
                {
                    onError?.Invoke(readTask.Exception);
                    return;
                }

                try
                {
                    var wrapper = JsonConvert.DeserializeObject<ApiResponseWrapper>(readTask.Result);
                    if (wrapper == null || wrapper.success == false)
                    {
                        throw new Exception(
                            $"Post request for {uri} failed: {((wrapper != null) ? wrapper.message : "JSON Decode Failed")}");
                    }
                    onSuccess?.Invoke(wrapper);
                }
                catch (Exception ex)
                {
                    onError?.Invoke(ex);
                }
            });
        });
    }
}