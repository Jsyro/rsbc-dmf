using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Rsbc.Dmf.CaseManagement.Service;
using Rsbc.Dmf.IcbcAdapter.Controllers;
using Xunit;
using CmsResultStatus = Rsbc.Dmf.CaseManagement.Service.ResultStatus;
using CmsResultStatusReply = Rsbc.Dmf.CaseManagement.Service.ResultStatusReply;

namespace Rsbc.Dmf.IcbcAdapter.Tests
{
    public class DmersControllerTests
    {
        [Fact]
        public async Task AddCaseForwardsPayloadAndCancellationInOneRpc()
        {
            var client = new Mock<CaseManager.CaseManagerClient>(MockBehavior.Strict);
            var dmerId = Guid.NewGuid();
            var caseId = Guid.NewGuid().ToString();
            using var cancellation = new CancellationTokenSource();
            client.Setup(c => c.AddCaseToDmerAsync(It.Is<AddCaseToDmerRequest>(r =>
                    r.DmerId == dmerId.ToString() && r.CaseType == "DMER" &&
                    r.Owner == "Intake Team" && r.DriverId == "1234567" &&
                    r.DocumentType == "DMER" && r.Comment == "Received"),
                    null, null, cancellation.Token))
                .Returns(UnaryCall(Task.FromResult(new CmsResultStatusReply { ResultStatus = CmsResultStatus.Success, Id = caseId })));
            using var provider = new ServiceCollection().AddSingleton(client.Object).BuildServiceProvider();
            var controller = CreateController(provider);

            var result = await controller.AddCaseToDmer(dmerId, new CreateDmerCaseBody
            {
                CaseType = "DMER",
                Owner = "Intake Team",
                DriverId = "1234567",
                DocumentType = "DMER",
                Comment = "Received"
            }, cancellation.Token);

            var json = Assert.IsType<JsonResult>(result);
            Assert.Equal(caseId, json.Value.GetType().GetProperty("case_id").GetValue(json.Value));
            client.VerifyAll();
            client.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData(StatusCode.InvalidArgument, 400)]
        [InlineData(StatusCode.NotFound, 404)]
        [InlineData(StatusCode.AlreadyExists, 409)]
        [InlineData(StatusCode.Internal, 500)]
        [InlineData(StatusCode.Unavailable, 502)]
        public async Task AddCaseMapsGrpcFailures(StatusCode grpcStatus, int expectedHttpStatus)
        {
            var client = new Mock<CaseManager.CaseManagerClient>(MockBehavior.Strict);
            client.Setup(c => c.AddCaseToDmerAsync(It.Is<AddCaseToDmerRequest>(r =>
                    r.CaseType == "DMER" && r.Owner == "" && r.DriverId == "" && r.DocumentType == "" && r.Comment == ""),
                    null, null, CancellationToken.None))
                .Returns(UnaryCall(Task.FromException<CmsResultStatusReply>(
                    new RpcException(new Status(grpcStatus, "CMS error")))));
            using var provider = new ServiceCollection().AddSingleton(client.Object).BuildServiceProvider();

            var result = await CreateController(provider).AddCaseToDmer(Guid.NewGuid(), new CreateDmerCaseBody(), CancellationToken.None);

            var problem = Assert.IsType<ObjectResult>(result);
            Assert.Equal(expectedHttpStatus, problem.StatusCode);
            Assert.Equal(expectedHttpStatus == 502 ? "Unable to add a case to the DMER." : "CMS error",
                Assert.IsType<ProblemDetails>(problem.Value).Title);
            client.VerifyAll();
            client.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData(CmsResultStatus.Fail, "")]
        [InlineData(CmsResultStatus.Success, "")]
        public async Task AddCaseRejectsUnsuccessfulReply(CmsResultStatus status, string id)
        {
            var client = new Mock<CaseManager.CaseManagerClient>(MockBehavior.Strict);
            client.Setup(c => c.AddCaseToDmerAsync(It.IsAny<AddCaseToDmerRequest>(), null, null, CancellationToken.None))
                .Returns(UnaryCall(Task.FromResult(new CmsResultStatusReply { ResultStatus = status, Id = id })));
            using var provider = new ServiceCollection().AddSingleton(client.Object).BuildServiceProvider();

            var result = await CreateController(provider).AddCaseToDmer(Guid.NewGuid(), new CreateDmerCaseBody(), CancellationToken.None);

            Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
        }

        [Fact]
        public async Task AddCaseDoesNotConvertCallerCancellationIntoHttpError()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var client = new Mock<CaseManager.CaseManagerClient>(MockBehavior.Strict);
            client.Setup(c => c.AddCaseToDmerAsync(It.IsAny<AddCaseToDmerRequest>(), null, null, cancellation.Token))
                .Returns(UnaryCall(Task.FromException<CmsResultStatusReply>(
                    new RpcException(new Status(StatusCode.Cancelled, "Cancelled")))));
            using var provider = new ServiceCollection().AddSingleton(client.Object).BuildServiceProvider();

            await Assert.ThrowsAsync<RpcException>(() =>
                CreateController(provider).AddCaseToDmer(Guid.NewGuid(), new CreateDmerCaseBody(), cancellation.Token));
        }

        private static DmersController CreateController(ServiceProvider provider)
        {
            return new DmersController(new ConfigurationBuilder().Build(), NullLogger<DmersController>.Instance,
                provider.GetRequiredService<IServiceScopeFactory>())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        private static AsyncUnaryCall<CmsResultStatusReply> UnaryCall(Task<CmsResultStatusReply> response)
        {
            return new AsyncUnaryCall<CmsResultStatusReply>(response, Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }
    }
}
