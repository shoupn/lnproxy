using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Invoicesrpc;
using Lnrpc;
using Routerrpc;
using LnProxyApi.Helpers;
using System.Text;

namespace LndGrpc
{
    public class LightningService
    {
        const int RoutingFeeBaseMsat = 1000; 
        const int RoutingFeePPM = 1000;
	    const int MinFeeBudgetMsat = 1000;
        const int ExpiryBuffer = 300;
        const int CltvDeltaAlpha = 42;
        const int CltvDeltaBeta = 42;
		const int MaxCltvExpiry = 1800;
		const int MinCltvExpiry =  200;


        private readonly Dictionary<Invoice.Types.InvoiceState, string> invoiceState = 
			new Dictionary<Invoice.Types.InvoiceState, string>
        {
            { Invoice.Types.InvoiceState.Canceled, "CANCELED" },
            { Invoice.Types.InvoiceState.Settled, "SETTLED" },
            { Invoice.Types.InvoiceState.Open, "OPEN" }
        };

        private LnGrpcClientService lnGrpcService;
        private readonly ILogger _logger;

        public LightningService(IConfiguration configuration, ILogger logger)
        {
            _logger = logger;
            lnGrpcService = new LnGrpcClientService(configuration);
        }


        public PayReq DecodePayRequest(string invoice)
        {
            try
            {
                var client = lnGrpcService.GetLightningClient();
                var payReq = new PayReqString
                {
                    PayReq = invoice,
                };

                var invoiceResponse = client.DecodePayReq(payReq);
                return invoiceResponse;
            }
            catch (Exception ex)
            {   
                _logger.LogError(ex, "Error decoding invoice");
                throw;
            }
        }

        public async Task SettleAndPayInvoice(PayReq originalInvoice, string request)
        {
            var routerClient = lnGrpcService.GetRouterClient();
            var req = new SendPaymentRequest()
            {
                PaymentRequest = request,
                TimeoutSeconds = 600
            };

            var call = routerClient.SendPaymentV2(req);
            await foreach (var payment in call.ResponseStream.ReadAllAsync())
            {
                try
                {	var invoiceClient = lnGrpcService.GetInvoiceClient();
                    if (payment.Status == Payment.Types.PaymentStatus.Failed ||
                         payment.Status == Payment.Types.PaymentStatus.Unknown)
                    {  
						var cancel = new CancelInvoiceMsg()
						{
							PaymentHash = HexStringHelper.HexStringToByteString(originalInvoice.PaymentHash)
						};
						var canceledRes = invoiceClient.CancelInvoice(cancel);
                        _logger.LogWarning("Canceled invoice", JsonSerializer.Serialize(canceledRes));
                    }

                    if (payment.Status == Payment.Types.PaymentStatus.Succeeded)
                    {
                        var s = new SettleInvoiceMsg
                        {
                            Preimage = HexStringHelper.HexStringToByteString(payment.PaymentPreimage)
                        };
                        var settled = invoiceClient.SettleInvoice(s);
                        _logger.LogInformation("Settled Invoice From payment {0}", JsonSerializer.Serialize(settled));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An exception occurred settling the invoice.");
                }
            }
        }

        public async Task SubscribeToHodlInvoice(ByteString rHash, PayReq originalInvoice, string originalRequest)
        {
            var invoiceClient = lnGrpcService.GetInvoiceClient();

            var sub = new SubscribeSingleInvoiceRequest() { RHash = rHash };
            var call = invoiceClient.SubscribeSingleInvoice(sub);
            await foreach (var invoice in call.ResponseStream.ReadAllAsync())
            {
                try
                {
                    if (invoice.State == Invoice.Types.InvoiceState.Accepted)
                    {
                        _logger.LogInformation($"Invoice was ACCEPTED", invoice);
                        await SettleAndPayInvoice(originalInvoice, originalRequest);
                    }
                    else if (invoiceState.TryGetValue(invoice.State, out string? stateString))
                    {
                        _logger.LogInformation($"Invoice was {stateString}", invoice);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An exception occurred settling invoice");
                }
            }
        }

        private long CalculateExpiry(PayReq payReqFromInvoice)
        {
            long expiry = payReqFromInvoice.Expiry;

            if (expiry > ExpiryBuffer)
            {
                expiry = ExpiryBuffer;
            }

            long currentUnixTime = (long)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long adjustedExpiry = (long)(payReqFromInvoice.Timestamp + expiry - currentUnixTime - ExpiryBuffer);

            return adjustedExpiry;
        }


        private async Task<RouteFeeResponse?> EstimateRouteFee(PayReq payReqFromInvoice)
        {
            try
            {
                var client = lnGrpcService.GetRouterClient();

                var route = new RouteFeeRequest()
                {   
                    AmtSat = payReqFromInvoice.NumSatoshis,
                    Dest =HexStringHelper.HexStringToByteString(payReqFromInvoice.Destination)
                };

                return await client.EstimateRouteFeeAsync(route);
            }
            catch (Exception ex)
            {   
                _logger.LogError(ex, "Error getting route fee");
                throw;
            }
        }

        private (int, Exception?) GetCustomMinFee(string? payReqRoutingMsat, PayReq payReqFromInvoice)
        {
            if (!string.IsNullOrWhiteSpace(payReqRoutingMsat))
            {
                var routing_fee_msat = RoutingFeeBaseMsat + payReqFromInvoice.NumMsat * RoutingFeePPM / 1_000_000;

                if (int.TryParse(payReqRoutingMsat, out int routingMsat) && routingMsat < (MinFeeBudgetMsat + routing_fee_msat))
                {
                    return (0, new Exception("custom fee budget too low"));
                }
                return ((int)(routingMsat - routing_fee_msat), null);
            }
            return (MinFeeBudgetMsat, null);
        }

        private long CalcCltvExpiry(RouteFeeResponse estimateFee){
            var cltvExpiry = estimateFee.TimeLockDelay + CltvDeltaAlpha + CltvDeltaBeta;

            if(cltvExpiry > MaxCltvExpiry){
                throw new Exception("CLTV expiry too high from estimate of routing fees");
            }

            if(cltvExpiry < MinCltvExpiry){
                cltvExpiry = MinCltvExpiry;
            }
            return cltvExpiry;
        }

        public AddHoldInvoiceResp CreateHodlInvoice(string payRequestString, string? payReqDescription, string? payReqHash, string? payReqRoutingMsat)
        {
            try
            {
                var payReqFromInvoice = DecodePayRequest(payRequestString);

                if (payReqFromInvoice.Timestamp + payReqFromInvoice.Expiry < (long)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ExpiryBuffer))
                {
                    throw new Exception("payment request expiration is too close.");
                }
                if (!string.IsNullOrWhiteSpace(payReqDescription) && !string.IsNullOrWhiteSpace(payReqHash))
                {
                    throw new Exception("Cannot set both Description and DescriptionHash");
                }
                if (payReqFromInvoice.Features.ContainsKey(30))
                {
                    throw new Exception("Cannot wrap AMP invoice");
                }
                if (payReqFromInvoice.NumMsat == 0)
                {
                    throw new Exception("Invoice must have a value");
                }

                var (customMinFee, customMinFeeError) = GetCustomMinFee(payReqRoutingMsat, payReqFromInvoice);
                if (customMinFeeError != null)
                {
                    throw customMinFeeError;
                }

                var estimateFee = EstimateRouteFee(payReqFromInvoice).Result;
                if(estimateFee == null){
                    throw new Exception("Error getting route fee from payment request");
                }

                var cltvExpiry = CalcCltvExpiry(estimateFee);

                if(cltvExpiry > MaxCltvExpiry){
                    throw new Exception("CLTV expiry too high from estimate of routing fees");
                }

                var invoiceClient = lnGrpcService.GetInvoiceClient();
                var hodlInvoice = new AddHoldInvoiceRequest()
                {
                    Memo = !string.IsNullOrWhiteSpace(payReqDescription) ?
                        payReqDescription : payReqFromInvoice.Description,
                    DescriptionHash = !string.IsNullOrWhiteSpace(payReqHash) ?
                        HexStringHelper.HexStringToByteString(payReqHash) : HexStringHelper.HexStringToByteString(payReqFromInvoice.DescriptionHash),
                    Hash = HexStringHelper.HexStringToByteString(payReqFromInvoice.PaymentHash),
                    ValueMsat = payReqFromInvoice.NumMsat,
                    CltvExpiry = (uint)cltvExpiry,
                    Expiry = CalculateExpiry(payReqFromInvoice)
                };

                var invoiceResponse = invoiceClient.AddHoldInvoice(hodlInvoice);
                _ = SubscribeToHodlInvoice(hodlInvoice.Hash, payReqFromInvoice, payRequestString);
                return invoiceResponse;
            }
            catch (Exception)
            {
                throw;
            }
        }
    }
}