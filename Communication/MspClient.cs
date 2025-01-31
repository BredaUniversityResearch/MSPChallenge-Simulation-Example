using System.Collections.Specialized;
using System.Net.Http.Headers;
using System.Web;
using MSPChallenge_Simulation_Example.Api;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MSPChallenge_Simulation_Example.Extensions;

namespace MSPChallenge_Simulation_Example.Communication;

public class MspClient
{
    private readonly string m_serverId;
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

    public MspClient(string serverId, string apiBaseUrl, ApiToken apiAccessToken, ApiToken apiRefreshToken)
    {
        // todo: implement token refresh
        m_serverId = serverId;
        m_client = new HttpClient
        {
            BaseAddress = new Uri(apiBaseUrl)
        };
        m_client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiAccessToken.token);
    }
    
    public void SetDefaultErrorHandler(Action<Exception>? onError)
    {
        m_defaultErrorHandler = onError;
    }
    
    public Task HttpPost(string uri, NameValueCollection postValues, NameValueCollection? headers = null)
    {
        return HttpPostInternal(uri, postValues, headers).ContinueWith(wrapperTask =>
        {
            if (null != wrapperTask.Exception)
            {
                throw wrapperTask.Exception;
            }            
            var wrapper = wrapperTask.Result;
            if (wrapper.success) return;
            var exception = new Exception(string.IsNullOrEmpty(wrapper.message) ? "Unknown error" : wrapper.message);
            m_defaultErrorHandler?.Invoke(exception);
            throw exception;
        });
    }    

    public Task<TTargetType> HttpPost<TTargetType>(string uri, NameValueCollection postValues, NameValueCollection? headers = null)
    {
        return HttpPostInternal(uri, postValues, headers).ContinueWith(wrapperTask =>
        {
            if (null != wrapperTask.Exception)
            {
                throw wrapperTask.Exception;
            }
            var wrapper = wrapperTask.Result;
            var result = wrapper.payload.ToObject<TTargetType>();
            try {
                if (result == null) throw new Exception($"Post request for {uri} failed: JSON Decode Failed");
                if (!wrapper.success)
                    throw new Exception(string.IsNullOrEmpty(wrapper.message) ? "Unknown error" : wrapper.message);
            } catch (Exception e) {
                m_defaultErrorHandler?.Invoke(e);
                throw;
            }
            return result;
        });
    }

    private Task<ApiResponseWrapper> HttpPostInternal(
        string uri,
        NameValueCollection postValues,
        NameValueCollection? headers = null
    )  {
        var xdebugTrigger = Environment.GetEnvironmentVariable("XDEBUG_TRIGGER"); // boolean
        if (!string.IsNullOrEmpty(xdebugTrigger))
        {        
            // add XDEBUG_TRIGGER as url query parameter to trigger xdebug
            uri = $"{uri}?XDEBUG_TRIGGER={HttpUtility.UrlEncode(xdebugTrigger)}";
        }
        var content = new FormUrlEncodedContent(postValues.AllKeys
            .Where(k => k != null)
            .ToDictionary(k => k!, k => postValues[k]!));
        var requestUri = m_client.BaseAddress != null ? new Uri(m_client.BaseAddress, uri.TrimStart('/')) : new Uri(uri);
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = content
        };
        if (headers != null)
        {
            foreach (var key in headers.AllKeys)
            {
                request.Headers.Add(key!, headers[key]);
            }
        }
        request.Headers.Add("X-Server-Id", m_serverId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return m_client.SendAsync(request).ContinueWith(postTask => 
        {
            if (postTask.IsFaulted)
            {
                m_defaultErrorHandler?.Invoke(postTask.Exception);
                throw postTask.Exception;
            }            
            var response = postTask.Result;
            if (!response.IsSuccessStatusCode)
            {
                var errorMessage = $"HTTP request failed with status code {response.StatusCode}";
                m_defaultErrorHandler?.Invoke(new HttpRequestException(errorMessage, null, response.StatusCode));
                throw new HttpRequestException(errorMessage, null, response.StatusCode);
            }            
            return response.Content.ReadAsStringAsync().ContinueWith(readTask =>
            {
                if (readTask.IsFaulted)
                {
                    m_defaultErrorHandler?.Invoke(readTask.Exception);
                    throw readTask.Exception;;
                }
                try
                {
                    var wrapper = JsonConvert.DeserializeObject<ApiResponseWrapper>(readTask.Result);
                    if (wrapper == null || wrapper.success == false)
                    {
                        throw new Exception(
                            $"Post request for {uri} failed: {((wrapper != null) ? wrapper.message : "JSON Decode Failed")}");
                    }
                    return wrapper;
                }
                catch (Exception ex)
                {
                    m_defaultErrorHandler?.Invoke(ex);
                    throw;
                }
            });
        }).Unwrap();
    }
}