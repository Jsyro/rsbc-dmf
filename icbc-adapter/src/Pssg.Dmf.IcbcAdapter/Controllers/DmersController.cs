using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Microsoft.IdentityModel.Tokens;
using Rsbc.Dmf.CaseManagement.Service;
using System;
using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Serilog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Server.HttpSys;
using Rsbc.Dmf.IcbcAdapter.ViewModels;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Grpc.Core;
using Microsoft.AspNetCore.StaticFiles;
using Swashbuckle.AspNetCore.Annotations;
using DocumentStorageClient = Pssg.DocumentStorageAdapter.DocumentStorageAdapter.DocumentStorageAdapterClient;
using System.Reflection.Metadata.Ecma335;
using Microsoft.AspNetCore.Http.HttpResults;
using Newtonsoft.Json;

namespace Rsbc.Dmf.IcbcAdapter.Controllers
{
    /// <summary>Identifies a file to download from document storage.</summary>
    public class DownloadRequestBody
    {
        /// <summary>
        /// Storage path of the file.
        /// </summary>
        [Required]
        public string FileUrl { get; init; }
    }

    /// <summary>Details for creating a case and linking it to a DMER.</summary>
    public class CreateDmerCaseBody
    {
        [JsonProperty("case_type")]
        public string CaseType { get; set; }

        /// <summary>Owning team name.</summary>
        [JsonProperty("owner")]
        public string Owner { get; set; }

        /// <summary>Driver licence number; only used when the DMER has no driver.</summary>
        [JsonProperty("driver_id")]
        public string DriverId { get; set; }

        [JsonProperty("document_type")]
        public string DocumentType { get; set; }

        [JsonProperty("comment")]
        public string Comment { get; set; }
    }

    [Authorize]
    [ApiController]
    [Route("[controller]")]
    public class DmersController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<DmersController> _logger;

        private readonly IServiceScopeFactory _serviceScopeFactory;


        public DmersController(IConfiguration configuration, ILogger<DmersController> logger, IServiceScopeFactory serviceScopeFactory)
        {
           _configuration = configuration;
           _logger = logger;
            _serviceScopeFactory = serviceScopeFactory;
        }


      
    /// <summary>
    /// GET: /dmers?pageSize=50&amp;cursor=0
    /// </summary>
    /// <returns>DMER items, the requested page_size, and a nextLink for full pages.</returns>
    /// <param name="pageSize">Maximum number of records to return</param>
    /// <param name="cursor">Zero-based offset of the first record in the page</param>
    [HttpGet("")]
    [Produces("application/json")]
    public ActionResult GetDmerBatch(
        [FromQuery, Range(1, 500)] int pageSize = 50,
        [FromQuery, Range(0, int.MaxValue)] int cursor = 0)
        {

            using var scope = _serviceScopeFactory.CreateScope();
            var documentManagerClient = scope.ServiceProvider.GetRequiredService<DocumentManager.DocumentManagerClient>();
            var dmerRecordsRequest = new GetDmerRecordsPaginatedRequest {PageSize = pageSize, Cursor = cursor }; 
            var dmerRecordsResults = documentManagerClient.GetDmerRecordsPaginated(dmerRecordsRequest);
            
            if (dmerRecordsResults.ResultStatus == CaseManagement.Service.ResultStatus.Fail)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, dmerRecordsResults);
            }
            var results = dmerRecordsResults.Items;
            var nextLink = results.Count == pageSize && cursor <= int.MaxValue - pageSize
                ? Url.Action(nameof(GetDmerBatch), new { pageSize, cursor = cursor + pageSize })
                : null;

            return Json(new DmerBatchResponse(results, pageSize, nextLink));
        }

  /// <summary>Downloads a file using the existing document storage adapter client.</summary>
        [HttpGet("{documentRecordId}/files")]
        [SwaggerOperation(Summary = "Download a document by dmerdocumentid", Description = "Returns the stored file as a download.")]
        [Produces("application/octet-stream")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
        public async Task<ActionResult> Download([FromRoute] Guid documentRecordId, CancellationToken cancellationToken)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var storageClient = scope.ServiceProvider.GetRequiredService<DocumentStorageClient>();
            var caseManagerClient = scope.ServiceProvider.GetRequiredService<CaseManager.CaseManagerClient>();
            Pssg.DocumentStorageAdapter.DownloadFileReply result;
            string fileUrl;

            // get file path from URL
            try
            {
                using var documentCall = caseManagerClient.GetLegacyDocumentAsync(
                    new LegacyDocumentRequest { DocumentId = documentRecordId.ToString() },
                    cancellationToken: cancellationToken);
                var documentReply = await documentCall.ResponseAsync;

                if (documentReply.ResultStatus != CaseManagement.Service.ResultStatus.Success)
                {
                    _logger.LogError("CMS adapter could not get document {DocumentRecordId}.", documentRecordId);
                    return Problem(statusCode: StatusCodes.Status500InternalServerError,
                        title: "Unable to get the document record.");
                }

                fileUrl = documentReply.Document?.DocumentUrl;
                if (string.IsNullOrEmpty(fileUrl))
                {
                    return Problem(statusCode: StatusCodes.Status404NotFound,
                        title: "The document record has no file path.");
                }
            }
            catch (RpcException exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(exception, "CMS adapter document request failed.");
                var statusCode = exception.StatusCode switch
                {
                    Grpc.Core.StatusCode.Unavailable => StatusCodes.Status503ServiceUnavailable,
                    Grpc.Core.StatusCode.DeadlineExceeded => StatusCodes.Status504GatewayTimeout,
                    _ => StatusCodes.Status502BadGateway
                };
                return Problem(statusCode: statusCode, title: "Unable to get the document record.");
            }
            // enforce type = DEMR
            // enforce state = Uploaded

            // get file package from S3
            try
            {
                using var call = storageClient.DownloadFileAsync(
                    new Pssg.DocumentStorageAdapter.DownloadFileRequest { ServerRelativeUrl = fileUrl },
                    cancellationToken: cancellationToken);
                result = await call.ResponseAsync;
            }
            catch (RpcException exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(exception, "Document storage adapter download request failed.");
                var statusCode = exception.StatusCode switch
                {
                    Grpc.Core.StatusCode.Unavailable => StatusCodes.Status503ServiceUnavailable,
                    Grpc.Core.StatusCode.DeadlineExceeded => StatusCodes.Status504GatewayTimeout,
                    _ => StatusCodes.Status502BadGateway
                };
                return Problem(statusCode: statusCode, title: "Unable to download the document from storage.");
            }

            if (result.ResultStatus != Pssg.DocumentStorageAdapter.ResultStatus.Success)
            {
                _logger.LogError("Document storage adapter could not download the document.");
                return Problem(statusCode: StatusCodes.Status500InternalServerError,
                    title: "Unable to download the document from storage.");
            }

            var fileName = Path.GetFileName(fileUrl);
            if (!new FileExtensionContentTypeProvider().TryGetContentType(fileName, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            return File(result.Data.ToByteArray(), contentType, fileName);
        }



              
    /// <summary>
    /// POST: /dmers/{dmerId}/add_case
    /// </summary>
    /// <returns>The id of the created case.</returns>
    /// <param name="dmerid">id (uuid) of the DMER document to create a case for</param>
    /// <param name="createDmerCaseBody">case details</param>
    /// <param name="cancellationToken">request cancellation token</param>
    [HttpPost("{dmerid}/add_case")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult> AddCaseToDmer([FromRoute] Guid dmerid, [FromBody] CreateDmerCaseBody createDmerCaseBody, CancellationToken cancellationToken)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var documentManagerClient = scope.ServiceProvider.GetRequiredService<DocumentManager.DocumentManagerClient>();
            var caseManagerClient = scope.ServiceProvider.GetRequiredService<CaseManager.CaseManagerClient>();

            try
            {
                // 1. load the DMER and its driver
                var documentReply = await caseManagerClient.GetLegacyDocumentAsync(
                    new LegacyDocumentRequest { DocumentId = dmerid.ToString() },
                    cancellationToken: cancellationToken);
                if (documentReply.ResultStatus != CaseManagement.Service.ResultStatus.Success)
                {
                    _logger.LogError("CMS adapter could not get document {DocumentRecordId}: {Error}", dmerid, documentReply.ErrorDetail);
                    return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Unable to get the DMER record.");
                }

                var document = documentReply.Document;
                if (document == null || string.IsNullOrEmpty(document.DocumentId))
                {
                    return Problem(statusCode: StatusCodes.Status404NotFound, title: "DMER record not found.");
                }

                if (!string.IsNullOrEmpty(document.CaseId))
                {
                    return Problem(statusCode: StatusCodes.Status409Conflict, title: "DMER is already linked to a case.");
                }

                var driverLicenseNumber = string.IsNullOrEmpty(document.Driver?.DriverLicenseNumber)
                    ? createDmerCaseBody.DriverId
                    : document.Driver.DriverLicenseNumber;
                if (string.IsNullOrEmpty(driverLicenseNumber))
                {
                    return Problem(statusCode: StatusCodes.Status400BadRequest, title: "DMER is not associated with a driver.");
                }

                // 2. create a case if the driver has none active
                var activeCasesReply = await caseManagerClient.GetActiveCasesAsync(
                    new DriverLicenseRequest { DriverLicenseNumber = driverLicenseNumber },
                    cancellationToken: cancellationToken);
                if (activeCasesReply.ResultStatus != CaseManagement.Service.ResultStatus.Success)
                {
                    _logger.LogError("CMS adapter could not get active cases: {Error}", activeCasesReply.ErrorDetail);
                    return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Unable to get the driver's active cases.");
                }

                if (activeCasesReply.Items.Count > 0)
                {
                    return Problem(statusCode: StatusCodes.Status409Conflict, title: "Driver already has an active case.");
                }

                var createCaseRequest = new CreateCaseRequest
                {
                    DriverLicenseNumber = driverLicenseNumber,
                    CaseTypeCode = string.IsNullOrWhiteSpace(createDmerCaseBody.CaseType) ? "DMER" : createDmerCaseBody.CaseType
                };
                if (!string.IsNullOrWhiteSpace(createDmerCaseBody.Owner))
                {
                    createCaseRequest.Owner = createDmerCaseBody.Owner;
                }

                var createCaseReply = await caseManagerClient.CreateCaseAsync(createCaseRequest, cancellationToken: cancellationToken);
                if (createCaseReply.ResultStatus != CaseManagement.Service.ResultStatus.Success || string.IsNullOrEmpty(createCaseReply.Id))
                {
                    _logger.LogError("CMS adapter could not create a case for DMER {DocumentRecordId}: {Error}", dmerid, createCaseReply.ErrorDetail);
                    return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Unable to create the case.");
                }

                var caseId = createCaseReply.Id;

                // 3. link the DMER to the new case and add the comment
                var updateDocumentReply = await documentManagerClient.UpdateDocumentAsync(
                    new UpdateDocumentRequest { Id = document.DocumentId, CaseId = caseId },
                    cancellationToken: cancellationToken);
                if (updateDocumentReply.ResultStatus != CaseManagement.Service.ResultStatus.Success)
                {
                    _logger.LogError("CMS adapter could not link DMER {DocumentRecordId} to case {CaseId}: {Error}", dmerid, caseId, updateDocumentReply.ErrorDetail);
                    return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Case created but the DMER could not be linked to it.");
                }

                if (!string.IsNullOrWhiteSpace(createDmerCaseBody.Comment))
                {
                    var commentReply = await caseManagerClient.CreateLegacyCaseCommentAsync(new LegacyComment
                    {
                        CaseId = caseId,
                        CommentText = createDmerCaseBody.Comment,
                        CommentTypeCode = "C",
                        CommentDate = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
                        Driver = new Driver { DriverLicenseNumber = driverLicenseNumber }
                    }, cancellationToken: cancellationToken);
                    if (commentReply.ResultStatus != CaseManagement.Service.ResultStatus.Success)
                    {
                        _logger.LogError("CMS adapter could not add comment to case {CaseId}: {Error}", caseId, commentReply.ErrorDetail);
                        return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Case created and DMER linked, but the comment could not be added.");
                    }
                }

                return Json(new { case_id = caseId });
            }
            catch (RpcException exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(exception, "CMS adapter request failed while adding a case to DMER {DocumentRecordId}.", dmerid);
                return Problem(statusCode: StatusCodes.Status502BadGateway, title: "Unable to add a case to the DMER.");
            }
        }
    }

}
