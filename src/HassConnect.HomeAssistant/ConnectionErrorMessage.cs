using System.Net;
using System.Text.Json;

namespace HassConnect.HomeAssistant;

public static class ConnectionErrorMessage
{
    public static string Describe(Exception error) => error switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
            "Access token rejected. Create a new token and try again.",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
            "Access was denied. Check this user's permissions and any proxy access rules.",
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
            "Home Assistant is receiving too many requests. Wait a moment, then try again.",
        HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError } =>
            "Home Assistant or its proxy is unavailable. Try again when the server is back online.",
        HttpRequestException { StatusCode: >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest } =>
            "The server redirected the request. Enter the direct Home Assistant address.",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
            "The Home Assistant API was not found at this address. Check the server address.",
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } =>
            "The server name could not be found. Check the address and your network connection.",
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } =>
            "A secure connection could not be established. Check the server's HTTPS certificate and your PC's date and time.",
        HttpRequestException =>
            "Could not reach Home Assistant. Check the server address and your network connection.",
        OperationCanceledException =>
            "Home Assistant did not respond in time. Check the connection and try again.",
        JsonException or InvalidDataException =>
            "The server did not return a valid Home Assistant response. Check the server address.",
        RegistrationRemovedException =>
            "This device was removed from Home Assistant. Reconnect to register it again.",
        _ => "The operation could not be completed. Try again or check the diagnostic logs."
    };
}
