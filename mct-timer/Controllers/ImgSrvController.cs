
using mct_timer.Models;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using Microsoft.Extensions.Options;
using Azure.Messaging.EventGrid;
using System.Linq;
using static System.Net.Mime.MediaTypeNames;


namespace mct_timer.Controllers
{
    public class ImgSrvController : Controller
    {
        private TelemetryClient _tmClient;
        private IBlobRepo _blRepo;
        private IGptImageGenerator _gptImage;
        private IOptions<ConfigMng> _config;

        public ImgSrvController(
            TelemetryClient tmClient,
            IOptions<ConfigMng> config,
            IGptImageGenerator gptImage,
            IBlobRepo blRepo) {

          _tmClient = tmClient;
            _blRepo = blRepo;
            _config = config;
            _gptImage = gptImage;
            ViewData["CDNUrl"] = _config.Value.WebCDN;
        }

        [HttpPost]
        public async Task<IActionResult> ImgTransform()        
        {
            BinaryData events = await BinaryData.FromStreamAsync(HttpContext.Request.Body);
            _tmClient.TrackTrace($"Received events: {events}");

            //return new OkObjectResult("OK");

            EventGridEvent[] eventGridEvents = EventGridEvent.ParseMany(events);

            foreach (EventGridEvent eventGridEvent in eventGridEvents)
            {
                // Handle system events
                if (eventGridEvent.TryGetSystemEventData(out object eventData))
                {
                    ImgSrvData data;
                    try
                    {
                       data = System.Text.Json.JsonSerializer.Deserialize<ImgSrvData>(Encoding.UTF8.GetString(eventGridEvent.Data));
                    }
                    catch (Exception ex)
                    {
                        _tmClient.TrackException(ex);
                        return new BadRequestObjectResult("Unrecognized event");
                    }

                    if (data.validationCode != null)
                        return new OkObjectResult("{'validationResponse':'" + data.validationCode + "'}");

                    var requesterName = _config.Value.StorageAccountName;
                   
                    if (!eventGridEvent.Topic.Contains(requesterName) || !eventGridEvent.Topic.Contains(_config.Value.SubscriptionID))
                    {
                        _tmClient.TrackException(new FormatException("event not recognized " + requesterName));
                        return new OkObjectResult("event not recognized "); //validation does not looks good but do not retry trigger
                    }
                    _tmClient.TrackTrace(data.url);

                    if (data != null && data.url.Contains(BlobRepo.LaregeImgfolder))
                    {
                        string fileName = Path.GetFileName(data.url);

                        var mResult = await _blRepo.TransformMediumFileAsync(fileName);
                        var sResult = await _blRepo.TransformSmallFileAsync(fileName);

                        _tmClient.TrackTrace($"Processed SubscriptionValidation event data with result: {sResult & mResult}, topic: {eventGridEvent.Topic}");                    }else if (data != null && data.url.Contains(BlobRepo.AiGenImgfolder))
                    {
                        string fileName = Path.GetFileName(data.url);
                        IDictionary<string,string> mdata = _blRepo.GetMetaData(Path.Combine(BlobRepo.AiGenImgfolder, fileName));
                        if (mdata != null && mdata.ContainsKey("prompt"))
                        {
                            string prompt = mdata["prompt"];
                            
                            // Log AI image generation request with metadata
                            var aiRequestProperties = new Dictionary<string, string>
                            {
                                { "fileName", fileName },
                                { "prompt", prompt },
                                { "user", mdata.ContainsKey("user") ? mdata["user"] : "unknown" },
                                { "author", mdata.ContainsKey("author") ? mdata["author"] : "unknown" },
                                { "requestTime", mdata.ContainsKey("when") ? mdata["when"] : "unknown" },
                                { "userIp", mdata.ContainsKey("IP") ? mdata["IP"] : "unknown" }
                            };
                            _tmClient.TrackEvent("AIImageGenerationRequested", aiRequestProperties);
                              // Validate prompt before sending to AI service
                            var validationResult = _gptImage.ValidatePrompt(prompt);
                            if (!validationResult.IsValid)
                            {
                                _tmClient.TrackTrace($"Prompt validation failed: {validationResult.Reason} - Prompt: {prompt}", 
                                    severityLevel: Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Warning);
                                aiRequestProperties["validationFailureReason"] = validationResult.Reason;
                                _tmClient.TrackEvent("AIImageGenerationValidationFailed", aiRequestProperties);
                                try
                                {
                                    await _blRepo.DeleteFileAsync((BlobRepo.AiGenImgfolder + fileName).ToLower());
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Error deleting temp AI file: {ex.Message}");
                                }
                                return new OkObjectResult("Prompt rejected: " + validationResult.Reason);
                            }                            try
                            {
                                var imggen = await _gptImage.GetImage(prompt);
                                mdata.Add("RevisedPrompt", imggen.RevisedPrompt);
                                await _blRepo.SaveImageAsync((BlobRepo.LaregeImgfolder + fileName).ToLower(), imggen.ImageBytes, (Dictionary<string,string>)mdata);
                                try
                                {
                                    var mResult = await _blRepo.DeleteFileAsync((BlobRepo.AiGenImgfolder + fileName).ToLower());
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Error deleting temp AI file after processing: {ex.Message}");
                                }

                                // Log successful image generation
                                var successProperties = new Dictionary<string, string>(aiRequestProperties)
                                {
                                    { "revisedPrompt", imggen.RevisedPrompt },
                                    { "imageSize", imggen.ImageBytes != null ? imggen.ImageBytes.ToArray().Length.ToString() : "0" },
                                    { "status", "success" }
                                };
                                _tmClient.TrackEvent("AIImageGenerationCompleted", successProperties);
                                _tmClient.TrackTrace($"Processed AI image generation: {fileName} with revised prompt: {imggen.RevisedPrompt}");
                            }
                            catch (ArgumentException ex)
                            {
                                _tmClient.TrackException(ex);
                                var errorProperties = new Dictionary<string, string>(aiRequestProperties)
                                {
                                    { "exceptionType", "ArgumentException" },
                                    { "errorMessage", ex.Message },
                                    { "status", "rejected" }
                                };
                                _tmClient.TrackEvent("AIImageGenerationRejected", errorProperties);
                                await _blRepo.DeleteFileAsync((BlobRepo.AiGenImgfolder + fileName).ToLower());
                                return new OkObjectResult("Image generation request rejected: " + ex.Message);
                            }
                            catch (Exception ex)
                            {
                                _tmClient.TrackException(ex);
                                var errorProperties = new Dictionary<string, string>(aiRequestProperties)
                                {
                                    { "exceptionType", ex.GetType().Name },
                                    { "errorMessage", ex.Message },
                                    { "stackTrace", ex.StackTrace ?? "no stack trace" },
                                    { "status", "failed" }
                                };
                                _tmClient.TrackEvent("AIImageGenerationFailed", errorProperties);
                                return new BadRequestObjectResult("Error processing image generation: " + ex.Message);
                            }
                        }
                    }
          
                }
            }
            return new OkObjectResult("OK"); // im ok, do not retry trigger
        }

    }
}
