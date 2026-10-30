using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Rsbc.Dmf.CaseManagement.Dynamics;
using Xunit;
using Cms = Rsbc.Dmf.CaseManagement.Service;

namespace Rsbc.Dmf.CaseManagement.Tests.Unit
{
    public class AddCaseToDmerTests
    {
        private readonly Mock<ICaseManager> caseManager = new Mock<ICaseManager>(MockBehavior.Strict);
        private readonly Mock<IDocumentManager> documentManager = new Mock<IDocumentManager>(MockBehavior.Strict);
        private readonly Cms.CaseService service;
        private readonly Guid documentId = Guid.NewGuid();
        private readonly Guid driverId = Guid.NewGuid();
        private readonly Guid caseId = Guid.NewGuid();
        private readonly LegacyDocument document;
        private readonly Cms.AddCaseToDmerRequest request;

        public AddCaseToDmerTests()
        {
            service = new Cms.CaseService(NullLogger<Cms.CaseService>.Instance,
                caseManager.Object, documentManager.Object, null, null);
            document = new LegacyDocument
            {
                DocumentId = documentId.ToString(),
                Driver = new Driver { DriverLicenseNumber = "1234567" },
                CreateDate = new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero),
                FaxReceivedDate = new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero)
            };
            request = new Cms.AddCaseToDmerRequest { DmerId = documentId.ToString() };

            documentManager.Setup(m => m.GetLegacyDocument(documentId.ToString())).ReturnsAsync(document);
            caseManager.Setup(m => m.GetDriverByLicenseNumber("1234567"))
                .ReturnsAsync(new[] { new Driver { Id = driverId.ToString() } });
            caseManager.Setup(m => m.GetCases(driverId, EntityState.Active, null))
                .ReturnsAsync(Array.Empty<CaseDetail>());
            caseManager.Setup(m => m.GetCases(driverId, EntityState.Active, "DMF"))
                .ReturnsAsync(Array.Empty<CaseDetail>());
            caseManager.Setup(m => m.CreateCase(It.IsAny<CreateCaseRequest>()))
                .ReturnsAsync(new ResultStatusReply { Success = true, Id = caseId.ToString() });
            caseManager.Setup(m => m.CreateCaseDocumentUrl(It.IsAny<CreateCaseDocumentUrlRequest>()))
                .Returns(Task.CompletedTask);
            documentManager.Setup(m => m.UpdateDocument(It.IsAny<UpdateDocumentCommand>())).Returns(true);
        }

        [Fact]
        public async Task CreatesCaseLinksExistingDocumentAndAddsCommentInOrder()
        {
            request.CaseType = "DMER";
            request.Owner = "Intake Team";
            request.DriverId = "7654321";
            request.DocumentType = "DMER";
            request.Comment = "Received DMER";
            var writes = new List<string>();
            caseManager.Setup(m => m.CreateCase(It.IsAny<CreateCaseRequest>()))
                .Callback<CreateCaseRequest>(r =>
                {
                    Assert.Equal("1234567", r.DriverLicenseNumber);
                    Assert.Equal("DMER", r.CaseTypeCode);
                    Assert.Equal("Intake Team", r.Owner);
                    Assert.Equal("DMF", r.ProgramArea);
                    Assert.Equal("DMER", r.TriggerType);
                    writes.Add("case");
                })
                .ReturnsAsync(new ResultStatusReply { Success = true, Id = caseId.ToString() });
            caseManager.Setup(m => m.CreateCaseDocumentUrl(It.IsAny<CreateCaseDocumentUrlRequest>()))
                .Callback<CreateCaseDocumentUrlRequest>(r =>
                {
                    Assert.Equal(caseId.ToString(), r.CaseId);
                    Assert.Equal("DMER", r.DocumentType);
                    Assert.Equal("1234567", r.DriverLicenseNumber);
                    Assert.Equal("Team - Intake", r.DocumentOwner);
                    Assert.Equal(document.FaxReceivedDate.Value.UtcDateTime, r.CreatedOn);
                    writes.Add("envelope");
                })
                .Returns(Task.CompletedTask);
            documentManager.Setup(m => m.UpdateDocument(It.IsAny<UpdateDocumentCommand>()))
                .Callback<UpdateDocumentCommand>(r =>
                {
                    Assert.Equal(documentId, r.Id);
                    Assert.Equal(caseId, r.CaseId);
                    Assert.Equal(0, r.SubmittalStatus);
                    Assert.Null(r.Queue);
                    Assert.Null(r.DpsPriority);
                    writes.Add("link");
                })
                .Returns(true);
            caseManager.Setup(m => m.CreateLegacyCaseComment(It.IsAny<LegacyComment>()))
                .Callback<LegacyComment>(r =>
                {
                    Assert.Equal(caseId.ToString(), r.CaseId);
                    Assert.Equal("Received DMER", r.CommentText);
                    Assert.Equal("C", r.CommentTypeCode);
                    Assert.Equal("1234567", r.Driver.DriverLicenseNumber);
                    Assert.InRange(r.CommentDate, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
                    writes.Add("comment");
                })
                .ReturnsAsync(new CreateStatusReply { Success = true });

            var reply = await service.AddCaseToDmer(request, null);

            Assert.Equal(Cms.ResultStatus.Success, reply.ResultStatus);
            Assert.Equal(caseId.ToString(), reply.Id);
            Assert.Equal(new[] { "case", "envelope", "link", "comment" }, writes);
        }

        [Theory]
        [InlineData("", "", "DMER", "")]
        [InlineData(" ", " ", "DMER", "")]
        [InlineData("REM", "", "REM", "Pending Rehab Review")]
        [InlineData("REM", "Custom Team", "REM", "Custom Team")]
        public async Task PreservesCaseDefaultsAndSkipsBlankComments(string caseType, string owner,
            string expectedType, string expectedOwner)
        {
            request.CaseType = caseType;
            request.Owner = owner;
            request.Comment = " ";

            await service.AddCaseToDmer(request, null);

            caseManager.Verify(m => m.CreateCase(It.Is<CreateCaseRequest>(r =>
                r.CaseTypeCode == expectedType && r.Owner == expectedOwner)), Times.Once);
            caseManager.Verify(m => m.CreateLegacyCaseComment(It.IsAny<LegacyComment>()), Times.Never);
        }

        [Fact]
        public async Task UsesFallbackLicenceWhenDocumentHasNoDriver()
        {
            document.Driver = null;
            request.DriverId = "1234567";

            await service.AddCaseToDmer(request, null);

            caseManager.Verify(m => m.CreateCase(It.Is<CreateCaseRequest>(r =>
                r.DriverLicenseNumber == "1234567")), Times.Once);
        }

        [Theory]
        [InlineData("", "DMER")]
        [InlineData(" ", "DMER")]
        [InlineData("001", "001")]
        public async Task CreatesEnvelopeWithPayloadDocumentTypeAndRecordDateFallback(string documentType, string expectedType)
        {
            request.DocumentType = documentType;
            document.FaxReceivedDate = null;

            await service.AddCaseToDmer(request, null);

            caseManager.Verify(m => m.CreateCaseDocumentUrl(It.Is<CreateCaseDocumentUrlRequest>(r =>
                r.DocumentType == expectedType && r.CreatedOn == document.CreateDate.UtcDateTime)), Times.Once);
        }

        [Fact]
        public async Task ReportsEnvelopeFailureWithoutLinkingExistingDocument()
        {
            caseManager.Setup(m => m.CreateCaseDocumentUrl(It.IsAny<CreateCaseDocumentUrlRequest>()))
                .ThrowsAsync(new InvalidOperationException("Envelope creation failed"));

            await AssertFailure(StatusCode.Internal, "Unable to create the case.");

            documentManager.Verify(m => m.UpdateDocument(It.IsAny<UpdateDocumentCommand>()), Times.Never);
        }

        [Fact]
        public async Task ReturnsExistingCaseIdFromCreateDmerCase()
        {
            caseManager.Setup(m => m.GetCases(driverId, EntityState.Active, "DMF"))
                .ReturnsAsync(new[] { new CaseDetail { CaseId = caseId.ToString() } });
            caseManager.Setup(m => m.CreateBringForward(It.IsAny<BringForwardRequest>()))
                .ReturnsAsync(new ResultStatusReply { Success = true });

            var reply = await service.CreateDmerCase(new Cms.CreateDmerCaseRequest
            {
                DriverLicenseNumber = "1234567",
                MedicalIssueDate = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(document.CreateDate)
            }, null);

            Assert.Equal(Cms.DmerStatusReply.Types.DmerResultStatus.Exists, reply.ResultStatus);
            Assert.Equal(caseId.ToString(), reply.Id);
            caseManager.Verify(m => m.CreateCase(It.IsAny<CreateCaseRequest>()), Times.Never);
        }

        [Fact]
        public async Task DoesNotLinkExistingDocumentWhenCreateDmerCaseFindsAnActiveCase()
        {
            caseManager.Setup(m => m.GetCases(driverId, EntityState.Active, "DMF"))
                .ReturnsAsync(new[] { new CaseDetail { CaseId = caseId.ToString() } });
            caseManager.Setup(m => m.CreateBringForward(It.IsAny<BringForwardRequest>()))
                .ReturnsAsync(new ResultStatusReply { Success = true });

            await AssertFailure(StatusCode.AlreadyExists, "Driver already has an active case.");

            VerifyNoWrites();
        }

        [Theory]
        [InlineData("")]
        [InlineData("invalid")]
        [InlineData("00000000-0000-0000-0000-000000000000")]
        public async Task RejectsInvalidDocumentId(string id)
        {
            request.DmerId = id;

            await AssertFailure(StatusCode.InvalidArgument, "A valid DMER document ID is required.");

            documentManager.Verify(m => m.GetLegacyDocument(It.IsAny<string>()), Times.Never);
            VerifyNoWrites();
        }

        [Fact]
        public async Task RejectsMissingDocument()
        {
            documentManager.Setup(m => m.GetLegacyDocument(documentId.ToString())).ReturnsAsync((LegacyDocument)null);

            await AssertFailure(StatusCode.NotFound, "DMER record not found.");
            VerifyNoWrites();
        }

        [Fact]
        public async Task RejectsAlreadyLinkedDocument()
        {
            document.CaseId = caseId.ToString();

            await AssertFailure(StatusCode.AlreadyExists, "DMER is already linked to a case.");
            VerifyNoWrites();
        }

        [Fact]
        public async Task RejectsMissingDriverLicence()
        {
            document.Driver = null;

            await AssertFailure(StatusCode.InvalidArgument, "DMER is not associated with a driver.");
            VerifyNoWrites();
        }

        [Fact]
        public async Task ReportsDriverLookupFailure()
        {
            caseManager.Setup(m => m.GetDriverByLicenseNumber("1234567")).ReturnsAsync(Array.Empty<Driver>());

            await AssertFailure(StatusCode.Internal, "Unable to get the driver's active cases.");
            VerifyNoWrites();
        }

        [Fact]
        public async Task RejectsDriverWithAnyActiveCase()
        {
            caseManager.Setup(m => m.GetCases(driverId, EntityState.Active, null))
                .ReturnsAsync(new[] { new CaseDetail() });

            await AssertFailure(StatusCode.AlreadyExists, "Driver already has an active case.");
            VerifyNoWrites();
        }

        [Fact]
        public async Task ReportsActiveCaseLookupFailure()
        {
            caseManager.Setup(m => m.GetCases(driverId, EntityState.Active, null))
                .ReturnsAsync((IEnumerable<CaseDetail>)null);

            await AssertFailure(StatusCode.Internal, "Unable to get the driver's active cases.");
            VerifyNoWrites();
        }

        [Theory]
        [InlineData(false, "")]
        [InlineData(true, "")]
        [InlineData(true, "invalid")]
        [InlineData(true, "00000000-0000-0000-0000-000000000000")]
        public async Task ReportsCaseCreationFailureWithoutLinking(bool success, string id)
        {
            caseManager.Setup(m => m.CreateCase(It.IsAny<CreateCaseRequest>()))
                .ReturnsAsync(new ResultStatusReply { Success = success, Id = id });

            await AssertFailure(StatusCode.Internal, "Unable to create the case.");

            documentManager.Verify(m => m.UpdateDocument(It.IsAny<UpdateDocumentCommand>()), Times.Never);
            caseManager.Verify(m => m.CreateLegacyCaseComment(It.IsAny<LegacyComment>()), Times.Never);
        }

        [Fact]
        public async Task ReportsLinkFailureWithoutAddingComment()
        {
            request.Comment = "Comment";
            documentManager.Setup(m => m.UpdateDocument(It.IsAny<UpdateDocumentCommand>())).Returns(false);

            await AssertFailure(StatusCode.Internal, "Case created but the DMER could not be linked to it.");

            caseManager.Verify(m => m.CreateLegacyCaseComment(It.IsAny<LegacyComment>()), Times.Never);
        }

        [Fact]
        public async Task ReportsCommentFailureAfterLinking()
        {
            request.Comment = "Comment";
            caseManager.Setup(m => m.CreateLegacyCaseComment(It.IsAny<LegacyComment>()))
                .ReturnsAsync(new CreateStatusReply { Success = false });

            await AssertFailure(StatusCode.Internal, "Case created and DMER linked, but the comment could not be added.");

            documentManager.Verify(m => m.UpdateDocument(It.IsAny<UpdateDocumentCommand>()), Times.Once);
        }

        [Fact]
        public async Task DoesNotExposeUnexpectedExceptionDetails()
        {
            documentManager.Setup(m => m.GetLegacyDocument(documentId.ToString()))
                .ThrowsAsync(new InvalidOperationException("Internal database details"));

            await AssertFailure(StatusCode.Internal, "Unable to get the DMER record.");
            VerifyNoWrites();
        }

        private async Task AssertFailure(StatusCode expectedStatus, string expectedMessage)
        {
            var exception = await Assert.ThrowsAsync<RpcException>(() => service.AddCaseToDmer(request, null));
            Assert.Equal(expectedStatus, exception.StatusCode);
            Assert.Equal(expectedMessage, exception.Status.Detail);
        }

        private void VerifyNoWrites()
        {
            caseManager.Verify(m => m.CreateCase(It.IsAny<CreateCaseRequest>()), Times.Never);
            documentManager.Verify(m => m.UpdateDocument(It.IsAny<UpdateDocumentCommand>()), Times.Never);
            caseManager.Verify(m => m.CreateLegacyCaseComment(It.IsAny<LegacyComment>()), Times.Never);
        }
    }
}
