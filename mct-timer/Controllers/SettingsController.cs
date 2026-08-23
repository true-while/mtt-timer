using Microsoft.AspNetCore.Mvc;
using mct_timer.Models;
using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.ApplicationInsights;
using Azure.Core;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.Web.CodeGenerators.Mvc.Templates.BlazorIdentity.Pages;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using System.Net;
using Microsoft.IdentityModel.Tokens;
using System.Data;
using Azure.Messaging.EventGrid;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Ixnas.AltchaNet;

namespace mct_timer.Controllers
{
    public class SettingsController : Controller
    {        private readonly TelemetryClient _logger;
        private readonly IOptions<ConfigMng> _config;
        private readonly IHttpContextAccessor _context;
        private readonly UsersContext _ac_context;        private readonly IGptImageGenerator _gptImage;
        private readonly IBlobRepo _blobRepo;
        private readonly string[] _permitedext = { ".jpeg", ".jpg", ".png" };
        private readonly string _tempFilePath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory)),"tmp");
        private readonly UploadValidator _validator;
        private readonly IPromptValidator _promptValidator;
        private readonly IKeyVaultMng _keyVaultMng;
        private readonly AltchaService _altcha;

        public string CDNUrl() { return _config.Value.WebCDN; }

        public SettingsController(
            TelemetryClient logger,
            IOptions<ConfigMng> config,
            IHttpContextAccessor context,
            UsersContext ac_context,
            UploadValidator validator,
            IBlobRepo blobRepo,
            IKeyVaultMng keyVault,
            IGptImageGenerator gptImage,
            IPromptValidator promptValidator,
            AltchaService altcha)
        {
            _logger = logger;
            _config = config;
            _context = context;
            _ac_context = ac_context;
            _blobRepo = blobRepo;
            _validator = validator;
            _promptValidator = promptValidator;
            _gptImage = gptImage;
            _keyVaultMng = keyVault;
            _altcha = altcha;

            if (AuthService.GetInstance == null)
                AuthService.Init(logger, config);
            _blobRepo = blobRepo;
        }


        public async Task<IActionResult> AvTest()
        {
            var avtest = new AvTest(_config, 
                _blobRepo, 
                _ac_context,
                _keyVaultMng,
                _gptImage);

            return View(avtest);
        }        [JwtAuthentication]
        [HttpGet]
        public async Task<IActionResult> DeleteBG(string bgid)
        {
            var token = _context?.HttpContext?.Request?.Cookies["jwt"];

            if (token != null)
            {
                JwtSecurityToken? jwt = null;
                var result = AuthService.GetInstance?.Validate(token, out jwt) ?? false;                    if (result && jwt != null)
                    {
                        var emailClaim = jwt.Claims?.FirstOrDefault(x => string.Compare(x.Type, "Email", true) == 0);
                        var email = emailClaim?.Value;
                        var user = !string.IsNullOrEmpty(email) ? await _ac_context.Users.FirstOrDefaultAsync(x => x.Email == email) : null;                    if (user != null && bgid!=null)                    {
                            if (user.Backgrounds?.Any(x => x.id == bgid && x.Locked != true) ?? false)
                            {
                                var bg = user.Backgrounds?.FirstOrDefault(x => x.id == bgid);
                                if (bg != null && !string.IsNullOrEmpty(bg.Url)) 
                                {
                                    try
                                    {
                                        await _blobRepo.DeleteImageAsync(bg.Url);
                                    }
                                    catch (Exception ex)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"Error deleting background image: {ex.Message}");
                                        // Continue even if delete fails - the background will be removed from user's list
                                    }
                                }
                                user.Backgrounds = user.Backgrounds?.Where(x => x.id != bgid).ToList() ?? new List<Background>();
                                _ac_context.Update(user);
                                await _ac_context.SaveChangesAsync();
                            }

                        int maxAI = AIAttempts();
                        int attemptsRemaining = user.HowManyActivityAllowed(maxAI);
                        bool canGenerateAI = user.IsAIActivityAllowed(maxAI);
                        string aiAvailableIn = user.WhenAIAvaiable(maxAI);
                        
                        ViewData["Attempts"] = attemptsRemaining;
                        ViewData["CanGenerateAI"] = canGenerateAI;
                        ViewData["AIAvailableIn"] = aiAvailableIn;
                        
                        var quote = user.GetQuote();
                        ViewData["UplodaQuote"] = quote;
                        ViewData["isUplodaQuote"] = quote?.Values?.Any(x => x < 5) ?? false;

                        return View("Custom", BgLinkPrep(user));
                    }

                    return RedirectToAction("Index", "Home");

                }
            }
            return new UnauthorizedResult();
        }        [JwtAuthentication]
        [HttpGet]
        public async Task<IActionResult> HideDefBG(string bgid, bool visible = false)
        {
            var token = _context?.HttpContext?.Request?.Cookies["jwt"];

            if (token != null)
            {
                JwtSecurityToken? jwt = null;
                var result = AuthService.GetInstance?.Validate(token, out jwt) ?? false;

                if (result && jwt != null)
                {
                    var emailClaim = jwt.Claims?.FirstOrDefault(x => string.Compare(x.Type, "Email", true) == 0);
                    var email = emailClaim?.Value;
                    var user = !string.IsNullOrEmpty(email) ? await _ac_context.Users.FirstOrDefaultAsync(x => x.Email == email) : null;                    if (user != null && bgid != null)
                    {
                        if (user.DefBGHidden == null) user.DefBGHidden = new Dictionary<string, bool>();
                        
                        if (user.DefBGHidden.ContainsKey(bgid))
                            user.DefBGHidden[bgid] = visible;
                        else
                            user.DefBGHidden.Add(bgid, visible);

                        _ac_context.Update(user);
                        await _ac_context.SaveChangesAsync();

                        user.CleanAllBG();
                        user.LoadDefaultBG();

                        return View("Default", BgLinkPrep(user));
                    }

                    return RedirectToAction("Index", "Home");

                    return RedirectToAction("Index", "Home");

                }
            }
            return new UnauthorizedResult();
        }        [JwtAuthentication]
        [HttpGet]
        public async Task<IActionResult> HideBG(string bgid, bool visible= false)
        {
            var token = _context?.HttpContext?.Request?.Cookies["jwt"];

            if (token != null)
            {
                JwtSecurityToken? jwt = null;
                var result = AuthService.GetInstance?.Validate(token, out jwt) ?? false;

                if (result && jwt != null)
                {
                    var emailClaim = jwt.Claims?.FirstOrDefault(x => string.Compare(x.Type, "Email", true) == 0);
                    var email = emailClaim?.Value;
                    var user = !string.IsNullOrEmpty(email) ? await _ac_context.Users.FirstOrDefaultAsync(x => x.Email == email) : null;                    if (user != null && bgid != null)                    {
                        var theBg = user.Backgrounds?.FirstOrDefault(x => x.id == bgid);
                        if (theBg != null)
                        {
                            theBg.Visible = visible;
                            _ac_context.Update(user);
                            await _ac_context.SaveChangesAsync();
                        }
                        
                        int maxAI = AIAttempts();
                        int attemptsRemaining = user.HowManyActivityAllowed(maxAI);
                        bool canGenerateAI = user.IsAIActivityAllowed(maxAI);
                        string aiAvailableIn = user.WhenAIAvaiable(maxAI);
                        
                        ViewData["Attempts"] = attemptsRemaining;
                        ViewData["CanGenerateAI"] = canGenerateAI;
                        ViewData["AIAvailableIn"] = aiAvailableIn;
                        
                        var quote = user.GetQuote();
                        ViewData["UplodaQuote"] = quote;
                        ViewData["isUplodaQuote"] = quote?.Values?.Any(x => x < 5) ?? false;

                        return View("Custom", BgLinkPrep(user));
                    }

                    return RedirectToAction("Index", "Home");

                }
            }
            return new UnauthorizedResult();
        }        [JwtAuthentication]
        [HttpPost]
        [DisableFormValueModelBinding]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadPhysical()
        {
            if (string.IsNullOrEmpty(Request.ContentType) || !MultipartRequestHelper.IsMultipartContentType(Request.ContentType))
            {
                ModelState.AddModelError("File",
                    $"The request couldn't be processed (Error 1).");

                return BadRequest(ModelState);
            }

            var boundary = MultipartRequestHelper.GetBoundary(
                MediaTypeHeaderValue.Parse(Request.ContentType),
                128); 
            var reader = new MultipartReader(boundary, HttpContext.Request.Body);
            var section = await reader.ReadNextSectionAsync();

            if (section != null)
            {
                var hasContentDispositionHeader =
                    ContentDispositionHeaderValue.TryParse(
                        section.ContentDisposition, out var contentDisposition);

                if (hasContentDispositionHeader && contentDisposition != null)
                {
                    // This check assumes that there's a file
                    // present without form data. If form data
                    // is present, this method immediately fails
                    // and returns the model error.
                    if (!MultipartRequestHelper
                        .HasFileContentDisposition(contentDisposition))
                    {
                        ModelState.AddModelError("File",
                            $"Did you select file for uploading?");
                        // Log error

                        return BadRequest(ModelState);
                    }
                    else
                    {
                        // Don't trust the file name sent by the client. To display
                        // the file name, HTML-encode the value.
                        var trustedFileNameForDisplay = WebUtility.HtmlEncode(
                                contentDisposition.FileName.Value);
                        var trustedFileNameForFileStorage = Path.GetRandomFileName();

                        // **WARNING!**
                        // In the following example, the file is saved without
                        // scanning the file's contents. In most production
                        // scenarios, an anti-virus/anti-malware scanner API
                        // is used on the file before making the file available
                        // for download or for use by other systems. 
                        // For more information, see the topic that accompanies 
                        // this sample.

                        var streamedFileContent = await _validator.ProcessStreamedFile(
                            section, contentDisposition, ModelState,
                            _permitedext, _config.Value.FileSizeLimit);

                        if (!ModelState.IsValid)
                        {
                            return BadRequest(ModelState);
                        }
                        Directory.CreateDirectory(_tempFilePath);

                        using (var targetStream = System.IO.File.Create(
                            Path.Combine(_tempFilePath, trustedFileNameForFileStorage)))
                        {
                            await targetStream.WriteAsync(streamedFileContent);
                            
                            TempData["UploadeFile"] = trustedFileNameForFileStorage;
                            TempData["UploadeName"] = trustedFileNameForDisplay;
                            _logger.TrackTrace( 
                                string.Format("Uploaded file '{0}' saved to '{1}' as {2}",
                                trustedFileNameForDisplay, _tempFilePath,
                                trustedFileNameForFileStorage));
                        }
                    }
                }

            }

            return new OkObjectResult(Json("{'File':[{'Successfully uploaded'}]}"));
        }
            [JwtAuthentication]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadBG([Bind("File", "Info", "BgType")] Background bg)
        {
            
            var token = _context?.HttpContext?.Request?.Cookies["jwt"];

            if (token != null)
            {
                JwtSecurityToken? jwt = null;
                var result = AuthService.GetInstance?.Validate(token, out jwt) ?? false;

                if (result && jwt != null)
                {
                    var emailClaim = jwt.Claims?.FirstOrDefault(x => string.Compare(x.Type, "Email", true) == 0);
                    var email = emailClaim?.Value;
                    var user = !string.IsNullOrEmpty(email) ? await _ac_context.Users.FirstOrDefaultAsync(x => x.Email == email) : null;

                    if (user != null && TempData.ContainsKey("UploadeFile") && TempData.ContainsKey("UploadeName"))
                    {

                        if (string.IsNullOrEmpty(bg.Info))
                        {
                            TempData["Error"] = "The images info should be provided.";
                        }                        else
                        {
                            bg.id = Guid.NewGuid().ToString();
                            bg.Author = user.Name;
                            bg.Visible = true;
                            
                            var uploadedFile = TempData["UploadeFile"] as string;
                            var uploadedName = TempData["UploadeName"] as string;
                            
                            if (!string.IsNullOrEmpty(uploadedFile) && !string.IsNullOrEmpty(uploadedName))
                            {
                                var file = Path.Combine(_tempFilePath, Path.GetFileName(uploadedFile));
                                var ext = Path.GetExtension(uploadedName);
                                if (System.IO.File.Exists(file))
                                {
                                    var mdata = new Dictionary<string, string>();
                                    try
                                    {
                                        var remoteIp = _context?.HttpContext?.Connection?.RemoteIpAddress?.ToString();
                                        if (!string.IsNullOrEmpty(remoteIp))
                                            mdata["IP"] = remoteIp;
                                    }
                                    catch
                                    {
                                        _logger.TrackTrace("Cannot detect IP");
                                    }
                                    mdata["user"] = user.Email ?? "";
                                    mdata["author"] = user.Name ?? "";
                                    mdata["when"] = DateTime.Now.ToString();

                                    var content = new BinaryData(System.IO.File.ReadAllBytes(file));
                                    var uri = await _blobRepo.SaveImageAsync((BlobRepo.LaregeImgfolder + bg.id + ext).ToLower(), content, mdata);

                                    System.IO.File.Delete(file);
                                    bg.Url = Path.GetFileName(uri.ToString());
                                    if (user.Backgrounds == null) user.Backgrounds = new List<Background>();
                                    user.Backgrounds.Add(bg);

                                    _ac_context.Update(user);
                                    await _ac_context.SaveChangesAsync();
                                }
                            }
                        }
                    }                    if (user != null)
                    {
                        int maxAI = AIAttempts();
                        int attemptsRemaining = user.HowManyActivityAllowed(maxAI);
                        bool canGenerateAI = user.IsAIActivityAllowed(maxAI);
                        string aiAvailableIn = user.WhenAIAvaiable(maxAI);
                        
                        ViewData["Attempts"] = attemptsRemaining;
                        ViewData["CanGenerateAI"] = canGenerateAI;
                        ViewData["AIAvailableIn"] = aiAvailableIn;
                        
                        var quote = user.GetQuote();
                        ViewData["UplodaQuote"] = quote;
                        ViewData["isUplodaQuote"] = quote?.Values.Any(x => x < 5) ?? false;
                        return View("Custom", BgLinkPrep(user));
                    }
                }
               
            }
            return new UnauthorizedResult();
        }
        [JwtAuthentication]        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateBG([Bind("Info", "BgType", "Altcha")] Background bg)
        {
            User? user = null;
            var token = _context?.HttpContext?.Request?.Cookies["jwt"];

            if (token != null)
            {
                JwtSecurityToken? jwt = null;
                var result = AuthService.GetInstance?.Validate(token, out jwt) ?? false;

                if (result && jwt != null)
                {
                    // Validate Altcha before proceeding
                    if (bg?.Altcha == null)
                    {
                        TempData["Error"] = "Please complete Altcha verification.";
                    }
                    else
                    {
                        var validationResult = await _altcha.Validate(bg.Altcha);
                        if (!validationResult.IsValid)
                        {
                            TempData["Error"] = "Altcha verification failed. Please try again.";
                            _logger.TrackEvent("AltchaValidationFailedForAIGeneration", new Dictionary<string, string> 
                            { 
                                { "user", jwt.Claims?.FirstOrDefault(x => x.Type == "Email")?.Value ?? "unknown" },
                                { "reason", "Invalid Altcha token" }
                            });
                        }
                        else
                        {
                            var emailClaim = jwt.Claims?.FirstOrDefault(x => string.Compare(x.Type, "Email", true) == 0);
                            var email = emailClaim?.Value;
                            user = !string.IsNullOrEmpty(email) ? await _ac_context.Users.FirstOrDefaultAsync(x => x.Email == email) : null;

                            if (user != null)
                            {
                                if (user.AIActivity == null)
                                    user.AIActivity = new List<DateTime>();

                                if (user.Backgrounds == null)
                                    user.Backgrounds = new List<Background>();

                                int maxAI;
                                if (!int.TryParse(_config.Value.MaxAIinTheDay, out maxAI)) maxAI = 5;

                                if (!user.IsAIActivityAllowed(maxAI))
                                {
                                    TempData["Error"] = $"You have already reach the limit of AI generated backgrounds. Please try again in {user.WhenAIAvaiable(maxAI)}";
                                }                                else if (string.IsNullOrEmpty(bg.Info))
                                {
                                    TempData["Error"] = $"The prompt for image generation must be provided.Please try again.";
                                }
                                else
                                {
                                    // Validate prompt using centralized validator
                                    var promptValidationResult = _promptValidator.ValidatePrompt(bg.Info);
                                    if (!promptValidationResult.IsValid)
                                    {
                                        TempData["Error"] = promptValidationResult.Reason;
                                        _logger.TrackEvent("PromptValidationFailedForAIGeneration", new Dictionary<string, string> 
                                        { 
                                            { "user", jwt.Claims?.FirstOrDefault(x => x.Type == "Email")?.Value ?? "unknown" },
                                            { "prompt", bg.Info },
                                            { "reason", promptValidationResult.Reason }
                                        });
                                    }
                                    else
                                    {
                                        bg.id = Guid.NewGuid().ToString();
                                        bg.Author = user.Name;
                                        bg.Visible = true;
                                        bg.Locked = false;
                                        bg.Url = $"{bg.id}.jpg";

                                        //save image
                                        var mdata = new Dictionary<string, string>();
                                        mdata["user"] = user.Name ?? "";
                                        mdata["when"] = DateTime.Now.ToString();
                                        mdata["prompt"] = bg.Info ?? "";
                                        try
                                        {
                                            var remoteIp = _context?.HttpContext?.Connection?.RemoteIpAddress?.ToString();
                                            mdata["IP"] = !string.IsNullOrEmpty(remoteIp) ? remoteIp : "none";
                                        }
                                        catch
                                        {
                                            mdata["IP"] = "none";
                                            _logger.TrackTrace("Cannot detect IP");
                                        }

                                        //register AI activity
                                        user.AIActivity.Add(DateTime.Now);

                                        //generate task
                                        await _blobRepo.SaveImageAsync((BlobRepo.AiGenImgfolder + bg.id + ".jpg").ToLower(), BinaryData.Empty, mdata);

                                        user.Backgrounds.Add(bg);

                                        _ac_context.Update(user);
                                        await _ac_context.SaveChangesAsync();
                                    }
                                }
                            }
                        }
                    }                    if (user != null)
                    {
                        int maxAI = AIAttempts();
                        int attemptsRemaining = user.HowManyActivityAllowed(maxAI);
                        bool canGenerateAI = user.IsAIActivityAllowed(maxAI);
                        string aiAvailableIn = user.WhenAIAvaiable(maxAI);
                        
                        ViewData["Attempts"] = attemptsRemaining;
                        ViewData["CanGenerateAI"] = canGenerateAI;
                        ViewData["AIAvailableIn"] = aiAvailableIn;
                        
                        var quote = user.GetQuote();
                        ViewData["UplodaQuote"] = quote;
                        ViewData["isUplodaQuote"] = quote?.Values?.Any(x => x < 5) ?? false;
                        return View("Custom", BgLinkPrep(user));
                    }
                }
            }
            return new UnauthorizedResult();
        }

        private int AIAttempts()
        {
            int maxAI;
            if (!int.TryParse(_config.Value.MaxAIinTheDay, out maxAI)) maxAI = 5;
            return maxAI;
        }
                private User? GetUserInfo()
        {
            var token = _context?.HttpContext?.Request?.Cookies["jwt"];

            if (token != null)
            {
                JwtSecurityToken? jwt = null;
                var result = AuthService.GetInstance?.Validate(token, out jwt) ?? false;

                if (result && jwt != null)
                {
                    var emailClaim = jwt.Claims?.FirstOrDefault(x => string.Compare(x.Type, "Email", true) == 0);
                    var email = emailClaim?.Value;
                    
                    if (!string.IsNullOrEmpty(email))
                    {
                        var user = _ac_context.Users.FirstOrDefaultAsync(x => x.Email == email).Result;
                        return user;
                    }
                }
            }

            return null;
        }

        [GenerateAntiforgeryTokenCookie]
        [JwtAuthentication]
        public IActionResult Default()
        {
            var user = GetUserInfo();

            if (user != null)
            {
                user.CleanAllBG();

                user.LoadDefaultBG(); //loading default BG

                return View(BgLinkPrep(user));
            }

            return new UnauthorizedResult();
        }

        [GenerateAntiforgeryTokenCookie]
        [JwtAuthentication]        public IActionResult Custom()
        {
            var user = GetUserInfo();
           

            if (user != null)
            {
                int maxAI = AIAttempts();
                int attemptsRemaining = user.HowManyActivityAllowed(maxAI);
                bool canGenerateAI = user.IsAIActivityAllowed(maxAI);
                string aiAvailableIn = user.WhenAIAvaiable(maxAI);
                
                ViewData["Attempts"] = attemptsRemaining; 
                ViewData["CanGenerateAI"] = canGenerateAI;
                ViewData["AIAvailableIn"] = aiAvailableIn;
                
                Dictionary<PresetType, int> quote = user.GetQuote();                
                ViewData["UplodaQuote"] = quote;
                ViewData["isUplodaQuote"] = (bool)quote.Values.Any(x => x < 5);
                return View(BgLinkPrep(user));
            }

            return new UnauthorizedResult();
        }

        [GenerateAntiforgeryTokenCookie]
        [JwtAuthentication]
        public IActionResult Index()
        {
            var user = GetUserInfo();

            if (user != null)
            {

                return View(user);
            }
            
            return new UnauthorizedResult();
        }

        [HttpPost]
        [GenerateAntiforgeryTokenCookie]
        [JwtAuthentication]
        public async Task<IActionResult> Index([Bind("Ampm", "Language", "DefTZ")] User updates)
        {
            var user = GetUserInfo();

            if (user != null)
            {
                user.Ampm = updates.Ampm;
                user.DefTZ = updates.DefTZ;
                user.Language = updates.Language;

                await _ac_context.SaveChangesAsync();

                return View(user);
            }

            return new UnauthorizedResult();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }        private User BgLinkPrep(User usr)
        {
            if (usr.Backgrounds == null)
                usr.Backgrounds = new List<Background>();

            foreach (var bg in usr.Backgrounds)
            {
                if (!string.IsNullOrEmpty(bg.Url))
                {
                    bg.Url = new Uri(new Uri(_config.Value.WebCDN), 
                        Path.Combine(BlobRepo.SmallImgfolder, Path.GetFileNameWithoutExtension(bg.Url) + ".png"))
                        .ToString();
                }
            }
            return usr;
        }
    }
}
