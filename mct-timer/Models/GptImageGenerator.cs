using Azure;
using OpenAI;
using OpenAI.Images;
using System.ClientModel;
using Microsoft.ApplicationInsights;

namespace mct_timer.Models
{     public interface IGptImageGenerator
    {
        public Task<GeneratedImage> GetImage(string promt);
        public bool TestConnection();
        public ValidationResult ValidatePrompt(string prompt);
    }    public class GptImageGenerator: IGptImageGenerator
    {
        string _endpoint;
        string _key;
        string _model;
        TelemetryClient _ai;
        IPromptValidator _validator;

        public GptImageGenerator(string endpoint, string key, string model, TelemetryClient ai) { 
            _endpoint = endpoint.TrimEnd('/');
            _key = key;
            _model = model;
            _ai = ai;
            _validator = new PromptValidator(ai);
        }

        public bool TestConnection()
        {
            try
            {
                var client = new ImageClient(
                    credential: new ApiKeyCredential(_key),
                    model: _model,
                    options: new OpenAIClientOptions()
                    {
                        Endpoint = new Uri(_endpoint),
                    }
                );
                return true;
            }
            catch (Exception ex)
            {
                _ai.TrackException(ex);
                return false;
            }
         }        public async Task<GeneratedImage> GetImage(string promt = "background image for my site")
        {    
            // Validate prompt before sending to AI service
            var validationResult = _validator.ValidatePrompt(promt);
            if (!validationResult.IsValid)
            {
                throw new ArgumentException($"Prompt validation failed: {validationResult.Reason}");
            }

            try
            {
                var client = new ImageClient(
                    credential: new ApiKeyCredential(_key),
                    model: _model,
                    options: new OpenAIClientOptions()
                    {
                        Endpoint = new Uri(_endpoint),
                    }
                );

                var options = new ImageGenerationOptions()
                {
                    Size = GeneratedImageSize.W1024xH1024,
                };

                GeneratedImage image = client.GenerateImage(promt, options);
                return image;
            }
            catch (Exception ex)
            {
                _ai.TrackException(ex);
                _ai.TrackTrace($"Image generation failed for model '{_model}' at endpoint '{_endpoint}'. Error: {ex.Message}. Inner: {ex.InnerException?.Message}", 
                    severityLevel: Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Error);
                throw;
            }
        }

        public ValidationResult ValidatePrompt(string prompt)
        {
            return _validator.ValidatePrompt(prompt);
        }
    }


}
