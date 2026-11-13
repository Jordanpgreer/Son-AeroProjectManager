using System.Data.Common;
using System.Net;

namespace SonAero.Platform.Integrations;

public sealed class FulcrumCoordinationUnavailableException(DbException innerException)
    : HttpRequestException("Fulcrum data is temporarily unavailable. Try again shortly.",
        innerException, HttpStatusCode.ServiceUnavailable);
