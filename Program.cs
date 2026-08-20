using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

//Get ACS Connection String from appsettings.json
var acsConnectionString = builder.Configuration.GetValue<string>("AcsConnectionString");
ArgumentNullException.ThrowIfNullOrEmpty(acsConnectionString);

//Call Automation Client
var client = new CallAutomationClient(connectionString: acsConnectionString);

//Grab the Cognitive Services endpoint from appsettings.json
var cognitiveServicesEndpoint = builder.Configuration.GetValue<string>("CognitiveServiceEndpoint");
ArgumentNullException.ThrowIfNullOrEmpty(cognitiveServicesEndpoint);

string answerPromptSystemTemplate = """ 
    You are an assisant designed to answer the customer query and analyze the sentiment score from the customer tone. 
    You also need to determine the intent of the customer query and classify it into categories such as sales, marketing, shopping, etc.
    This is a phone call — your reply is read aloud by text-to-speech. Never include citations, sources, links, markdown formatting, or URLs of any kind.
    Detect whether the customer's query is in Arabic or English, and reply in that same language.
    Always answer what the customer actually asked first. Only add a brief closing offer of further help ("في شي ثاني؟" or similar) when it naturally fits — do not repeat that closing line on every single turn like a fixed script, and never reply with only a closing line and no real answer.
    This conversation has multiple turns; use the prior turns already in this conversation for context instead of treating each message as standalone.
    If Arabic, reply the way a Gulf (Saudi/Kuwaiti/Emirati) customer-service agent naturally speaks on the phone — everyday spoken Gulf words and sentence structure, not textbook Modern Standard Arabic and not heavy street slang. This applies to EVERY turn of the ENTIRE call, from the first reply to the last, even after many turns — do not drift back into formal Arabic as the conversation goes on. Always use these Gulf words instead of their formal equivalents, with no exceptions: "وش" not "ماذا", "كيف أقدر أساعدك" not "كيف يمكنني مساعدتك"/"كيف أستطيع خدمتك", "أبغى/تبغى" not "أريد/تريد", "أبشر" not "بالتأكيد"/"حسناً", "زين/تمام" not "جيد", "شنو" or "وش" not "ما هو", "عطني" not "أعطني". Follow the style of these examples exactly:
    - Customer: "أبغى أسوي حساب جديد" -> Agent: "أبشر، عطني اسمك وإيميلك وأسوي لك الحساب الحين."
    - Customer: "وش الأسعار عندكم" -> Agent: "عندنا أكثر من باقة، تبغى أرسل لك التفاصيل على رقمك أو إيميلك؟"
    - Customer: "ما ضبط معاي تسجيل الدخول" -> Agent: "لا تشيل هم، جرب تعيد كلمة المرور من زر نسيت كلمة المرور وأبشر أذا احتجت مساعدة زيادة."
    - Customer: "مشكور، هذا كل شي" -> Agent: "العفو، يعطيك العافية ويوم سعيد."
    - Customer: "طيب" -> Agent: "تمام، طال عمرك، شنو الشي اللي تحتاج مساعدة فيه بالضبط؟"
    Match that tone and word choice. Keep sentences short, avoid rare/regional slang words and unusual contractions (they trip up the voice reading this aloud), avoid English loanword transliterations (write "كلمة المرور" not "الباسورد"), and avoid slashes, parentheses, quotation marks, or Latin punctuation — use plain Arabic sentences and Arabic punctuation (، and ؟) only.
    Decide whether the customer explicitly wants to talk to a human agent/representative; if so, set escalate to true.
    Decide whether the customer is done with the call — they said bye, thanks that's all, no that's it, or similar, and have nothing left to ask; if so, set endCall to true and make content a short warm goodbye in the detected language instead of an answer.
    Use a scale of 1-10 (10 being highest) to rate the sentiment score.
    Respond with ONLY a single minified JSON object and nothing else — no markdown, no code fences, no extra commentary before or after it. Use exactly these keys:
    {"content": "<a direct answer to the customer's query in one or two short sentences, in the detected language>", "score": <sentiment score 1-10>, "intent": "<short intent label>", "category": "<one of: sales, marketing, shopping, support, other>", "language": "<'ar' or 'en'>", "escalate": <true or false>, "endCall": <true or false>}
    """;

string helloPrompt = "Hello, thank you for calling! How can I help you today?";
string timeoutSilencePrompt = "I’m sorry, I didn’t hear anything. If you need assistance please let me know how I can help you.";
string goodbyePrompt = "Thank you for calling! I hope I was able to assist you. Have a great day!";
string callTransferFailurePrompt = "It looks like all I can’t connect you to an agent right now, but we will get the next available agent to call you back as soon as possible.";
string agentPhoneNumberEmptyPrompt = "I’m sorry, we're currently experiencing high call volumes and all of our agents are currently busy. Our next available agent will call you back as soon as possible.";
string EndCallPhraseToConnectAgent = "Sure, please stay on the line. I’m going to transfer you to an agent.";

string transferFailedContext = "TransferFailed";
string connectAgentContext = "ConnectAgent";
string goodbyeContext = "Goodbye";

string agentPhonenumber = builder.Configuration.GetValue<string>("AgentPhoneNumber");

// Foundry project endpoint, e.g. https://<resource>.services.ai.azure.com/api/projects/<project>
var foundryProjectEndpoint = builder.Configuration.GetValue<string>("FoundryProjectEndpoint");
ArgumentNullException.ThrowIfNullOrEmpty(foundryProjectEndpoint);

// Agent to invoke via agent_reference on the project's Responses endpoint (gpt-5 backed)
var foundryAgentName = builder.Configuration.GetValue<string>("FoundryAgentName");
ArgumentNullException.ThrowIfNullOrEmpty(foundryAgentName);
var foundryAgentVersion = builder.Configuration.GetValue<string>("FoundryAgentVersion");

var foundryResponsesUri = new Uri(new Uri(foundryProjectEndpoint.TrimEnd('/') + "/"), "openai/v1/responses");

// Agent invocation only supports Microsoft Entra ID auth, not API keys
var foundryCredential = new Azure.Identity.DefaultAzureCredential();
var foundryHttpClient = new HttpClient();

//Register and make CallAutomationClient accessible via dependency injection
builder.Services.AddSingleton(client);
var app = builder.Build();

var devTunnelUri = builder.Configuration.GetValue<string>("DevTunnelUri");
ArgumentNullException.ThrowIfNullOrEmpty(devTunnelUri);
var maxTimeout = 2;

// Tracks the Foundry Responses conversation per call so each turn has context from prior turns
var conversationState = new ConcurrentDictionary<string, string>();

app.MapGet("/", () => "Hello ACS CallAutomation!");

app.MapPost("/api/incomingCall", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        logger.LogInformation($"Incoming Call event received.");

        // Handle system events
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            // Handle the subscription validation event.
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                var responseData = new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                };
                return Results.Ok(responseData);
            }
        }

        var jsonObject = Helper.GetJsonObject(eventGridEvent.Data);
        var callerId = Helper.GetCallerId(jsonObject);
        var incomingCallContext = Helper.GetIncomingCallContext(jsonObject);
        var callbackUri = new Uri(new Uri(devTunnelUri), $"/api/callbacks/{Guid.NewGuid()}?callerId={callerId}");
        Console.WriteLine($"Callback Url: {callbackUri}");
        var options = new AnswerCallOptions(incomingCallContext, callbackUri)
        {
            CallIntelligenceOptions = new CallIntelligenceOptions() { CognitiveServicesEndpoint = new Uri(cognitiveServicesEndpoint) }
        };

        AnswerCallResult answerCallResult = await client.AnswerCallAsync(options);
        Console.WriteLine($"Answered call for connection id: {answerCallResult.CallConnection.CallConnectionId}");

        //Use EventProcessor to process CallConnected event
        var answer_result = await answerCallResult.WaitForEventProcessorAsync();
        if (answer_result.IsSuccess)
        {
            Console.WriteLine($"Call connected event received for connection id: {answer_result.SuccessResult.CallConnectionId}");
            var callConnectionMedia = answerCallResult.CallConnection.GetCallMedia();
            await HandleRecognizeAsync(callConnectionMedia, callerId, helloPrompt, BuildBilingualGreetingSsml());
        }

        client.GetEventProcessor().AttachOngoingEventProcessor<PlayCompleted>(answerCallResult.CallConnection.CallConnectionId, async (playCompletedEvent) =>
        {
            logger.LogInformation($"Play completed event received for connection id: {playCompletedEvent.CallConnectionId}, context: '{playCompletedEvent.OperationContext}'.");
            try
            {
                if (!string.IsNullOrWhiteSpace(playCompletedEvent.OperationContext) && (playCompletedEvent.OperationContext.Equals(transferFailedContext, StringComparison.OrdinalIgnoreCase)
                || playCompletedEvent.OperationContext.Equals(goodbyeContext, StringComparison.OrdinalIgnoreCase)))
                {
                    logger.LogInformation($"Disconnecting the call for connection id: {playCompletedEvent.CallConnectionId}...");
                    await answerCallResult.CallConnection.HangUpAsync(true);
                    logger.LogInformation($"Hang up requested successfully for connection id: {playCompletedEvent.CallConnectionId}.");
                }
                else if (!string.IsNullOrWhiteSpace(playCompletedEvent.OperationContext) && playCompletedEvent.OperationContext.Equals(connectAgentContext, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(agentPhonenumber) || !Regex.IsMatch(agentPhonenumber, @"^\+[1-9]\d{1,14}$"))
                    {
                        logger.LogInformation($"Agent phone number is empty or not in E.164 format");
                        await HandlePlayAsync(agentPhoneNumberEmptyPrompt,
                          transferFailedContext, answerCallResult.CallConnection.GetCallMedia());
                    }
                    else
                    {
                        try
                        {
                            logger.LogInformation($"Initializing the Call transfer...");
                            CommunicationIdentifier transferDestination = new PhoneNumberIdentifier(agentPhonenumber);
                            TransferCallToParticipantResult result = await answerCallResult.CallConnection.TransferCallToParticipantAsync(transferDestination);
                            logger.LogInformation($"Transfer call initiated: {result.OperationContext}");
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Call transfer failed for connection id: {ConnectionId}", playCompletedEvent.CallConnectionId);
                            await HandlePlayAsync(callTransferFailurePrompt, transferFailedContext, answerCallResult.CallConnection.GetCallMedia());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // HangUpAsync (or the fallback prompts above) can throw if the call already ended on the caller's side
                logger.LogError(ex, "Error handling PlayCompleted for connection id: {ConnectionId}, context: '{Context}'", playCompletedEvent.CallConnectionId, playCompletedEvent.OperationContext);
            }
        });

        client.GetEventProcessor().AttachOngoingEventProcessor<PlayFailed>(answerCallResult.CallConnection.CallConnectionId, async (playFailedEvent) =>
        {
            logger.LogInformation($"Play failed event received for connection id: {playFailedEvent.CallConnectionId}. Hanging up call...");
            try
            {
                await answerCallResult.CallConnection.HangUpAsync(true);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to hang up after PlayFailed for connection id: {ConnectionId}", playFailedEvent.CallConnectionId);
            }
        });
        client.GetEventProcessor().AttachOngoingEventProcessor<CallTransferAccepted>(answerCallResult.CallConnection.CallConnectionId, async (callTransferAcceptedEvent) =>
        {
            logger.LogInformation($"Call transfer accepted event received for connection id: {callTransferAcceptedEvent.CallConnectionId}.");
        });
        client.GetEventProcessor().AttachOngoingEventProcessor<CallTransferFailed>(answerCallResult.CallConnection.CallConnectionId, async (callTransferFailedEvent) =>
        {
            logger.LogInformation($"Call transfer failed event received for connection id: {callTransferFailedEvent.CallConnectionId}.");
            var resultInformation = callTransferFailedEvent.ResultInformation;
            logger.LogError("Encountered error during call transfer, message={msg}, code={code}, subCode={subCode}", resultInformation?.Message, resultInformation?.Code, resultInformation?.SubCode);

            await HandlePlayAsync(callTransferFailurePrompt,
                       transferFailedContext, answerCallResult.CallConnection.GetCallMedia());

        });
        client.GetEventProcessor().AttachOngoingEventProcessor<RecognizeCompleted>(answerCallResult.CallConnection.CallConnectionId, async (recognizeCompletedEvent) =>
        {
            Console.WriteLine($"Recognize completed event received for connection id: {recognizeCompletedEvent.CallConnectionId}");
            var speech_result = recognizeCompletedEvent.RecognizeResult as SpeechResult;
            if (!string.IsNullOrWhiteSpace(speech_result?.Speech))
            {
                Console.WriteLine($"Recognized speech: {speech_result.Speech}");

                try
                {
                    var reply = await GetChatGPTResponse(speech_result.Speech, recognizeCompletedEvent.CallConnectionId);
                    logger.LogInformation("Agent reply: Content={ans}, Score={rating}, Intent={Int}, Category={cat}, Language={lang}, Escalate={esc}",
                        reply.Content, reply.Score, reply.Intent, reply.Category, reply.Language, reply.Escalate);

                    if (reply.Escalate)
                    {
                        await HandlePlayAsync(EndCallPhraseToConnectAgent,
                                   connectAgentContext, answerCallResult.CallConnection.GetCallMedia());
                    }
                    else if (reply.EndCall)
                    {
                        // Play the farewell and stop listening so PlayCompleted's goodbyeContext hangs up the call
                        await HandlePlayAsync(reply.Content, goodbyeContext, answerCallResult.CallConnection.GetCallMedia(),
                            voiceName: GetVoiceNameForLanguage(reply.Language), sourceLocale: GetSourceLocaleForLanguage(reply.Language));
                    }
                    else
                    {
                        await HandleChatResponse(reply.Content, answerCallResult.CallConnection.GetCallMedia(), callerId, logger, voiceName: GetVoiceNameForLanguage(reply.Language), sourceLocale: GetSourceLocaleForLanguage(reply.Language));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to process recognized speech for connection id: {ConnectionId}", recognizeCompletedEvent.CallConnectionId);
                    try
                    {
                        await HandlePlayAsync(callTransferFailurePrompt, transferFailedContext, answerCallResult.CallConnection.GetCallMedia());
                    }
                    catch (Exception playEx)
                    {
                        // Call may have already ended (e.g. caller hung up) by the time we try to play the fallback prompt
                        logger.LogError(playEx, "Failed to play fallback prompt for connection id: {ConnectionId}", recognizeCompletedEvent.CallConnectionId);
                    }
                }
            }
        });

        client.GetEventProcessor().AttachOngoingEventProcessor<RecognizeFailed>(answerCallResult.CallConnection.CallConnectionId, async (recognizeFailedEvent) =>
        {
            var callConnectionMedia = answerCallResult.CallConnection.GetCallMedia();

            if (MediaEventReasonCode.RecognizeInitialSilenceTimedOut.Equals(recognizeFailedEvent.ResultInformation.SubCode.Value.ToString()) && maxTimeout > 0)
            {
                Console.WriteLine($"Recognize failed event received for connection id: {recognizeFailedEvent.CallConnectionId}. Retrying recognize...");
                maxTimeout--;
                await HandleRecognizeAsync(callConnectionMedia, callerId, timeoutSilencePrompt);
            }
            else
            {
                Console.WriteLine($"Recognize failed event received for connection id: {recognizeFailedEvent.CallConnectionId}. Playing goodbye message...");
                await HandlePlayAsync(goodbyePrompt, goodbyeContext, callConnectionMedia);
            }
        });
    }
    return Results.Ok();
});

// api to handle call back events
app.MapPost("/api/callbacks/{contextId}", async (
    [FromBody] CloudEvent[] cloudEvents,
    [FromRoute] string contextId,
    [Required] string callerId,
    CallAutomationClient callAutomationClient,
    ILogger<Program> logger) =>
{
    var eventProcessor = client.GetEventProcessor();
    eventProcessor.ProcessEvents(cloudEvents);
    return Results.Ok();
});

async Task HandleChatResponse(string chatResponse, CallMedia callConnectionMedia, string callerId, ILogger logger, string context = "OpenAISample", string voiceName = "en-US-NancyNeural", string sourceLocale = "en-US")
{
    var chatGPTResponseSource = new TextSource(SanitizeForSpeech(chatResponse))
    {
        VoiceName = voiceName,
        SourceLocale = sourceLocale
    };

    var recognizeOptions =
        new CallMediaRecognizeSpeechOptions(
            targetParticipant: CommunicationIdentifier.FromRawId(callerId))
        {
            InterruptPrompt = false,
            InitialSilenceTimeout = TimeSpan.FromSeconds(15),
            Prompt = chatGPTResponseSource,
            SpeechLanguages = new List<string> { "ar-SA", "en-US" },
            OperationContext = context,
            EndSilenceTimeout = TimeSpan.FromMilliseconds(500)
        };

    var recognize_result = await callConnectionMedia.StartRecognizingAsync(recognizeOptions);
}

// Gulf Arabic voice paired with the existing English voice for language-adaptive playback
string GetVoiceNameForLanguage(string language) =>
    language.Trim().ToLowerInvariant().StartsWith("ar") ? "ar-SA-HamedNeural" : "en-US-NancyNeural";

// TextSource needs an explicit SourceLocale (not just VoiceName) or it can normalize/pronounce
// Arabic text under the wrong locale context, same issue that affected the SSML greeting.
string GetSourceLocaleForLanguage(string language) =>
    language.Trim().ToLowerInvariant().StartsWith("ar") ? "ar-SA" : "en-US";

// Normalize characters the neural voice mispronounces or pauses awkwardly on (smart quotes, slashes, em-dashes)
string SanitizeForSpeech(string text) =>
    Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1") // markdown links: keep the label, drop the URL
        .Replace("\u201C", "").Replace("\u201D", "").Replace("\"", "")
        .Replace("\u2014", ",").Replace("\u2013", ",").Replace("-", " ")
        .Replace("/", " أو ").Replace("(", "").Replace(")", "")
        .Trim();

async Task<AgentReply> GetChatGPTResponse(string speech_input, string callConnectionId)
{
    conversationState.TryGetValue(callConnectionId, out var previousResponseId);
    var (raw, responseId) = await GetChatCompletionsAsync(answerPromptSystemTemplate, speech_input, previousResponseId);
    if (!string.IsNullOrWhiteSpace(responseId))
    {
        conversationState[callConnectionId] = responseId;
    }
    return ParseAgentReply(raw);
}

AgentReply ParseAgentReply(string rawResponse)
{
    var jsonText = ExtractJsonObject(rawResponse);
    try
    {
        var reply = JsonSerializer.Deserialize<AgentReply>(jsonText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (reply != null && !string.IsNullOrWhiteSpace(reply.Content))
        {
            if (reply.Language.Trim().ToLowerInvariant().StartsWith("ar"))
            {
                reply.Content = ApplyGulfDialectSubstitutions(reply.Content);
            }
            return reply;
        }
    }
    catch (JsonException)
    {
        // Agent didn't return valid JSON; fall through to the raw-text fallback below
    }

    return new AgentReply { Content = "آسف، ما فهمت عليك، ممكن تعيد كلامك مرة ثانية؟", Score = 5, Language = "ar" };
}

// Deterministic safety net: swap common MSA words for Gulf equivalents regardless of what the
// model actually generated, since prompting alone hasn't reliably produced Gulf dialect.
string ApplyGulfDialectSubstitutions(string text)
{
    var substitutions = new (string Pattern, string Replacement)[]
    {
        (@"\bماذا\b", "وش"),
        (@"\bكيف يمكنني\b", "كيف أقدر"),
        (@"\bكيف أستطيع\b", "كيف أقدر"),
        (@"\bأريد\b", "أبغى"),
        (@"\bتريد\b", "تبغى"),
        (@"\bلا أستطيع\b", "ما أقدر"),
        (@"\bهل يمكنك\b", "تقدر"),
        (@"\bشكراً جزيلاً\b", "يعطيك العافية"),
        (@"\bشكرا جزيلا\b", "يعطيك العافية"),
        (@"\bلا بأس\b", "ما فيه شي"),
        (@"\bحسناً\b", "تمام"),
        (@"\bحسنا\b", "تمام"),
        (@"\bبالتأكيد\b", "أبشر"),
        (@"\bما هو\b", "وش"),
        (@"\bما هي\b", "وش"),
        (@"\bأعطني\b", "عطني"),
        (@"\bكيف حالك\b", "شلونك"),
    };

    foreach (var (pattern, replacement) in substitutions)
    {
        text = Regex.Replace(text, pattern, replacement);
    }

    return text;
}

// Extracts only the first balanced JSON object, since a degenerate/repetitive model response can
// concatenate the same object multiple times (e.g. "{...}{...}{...}"), which would otherwise be
// treated as one giant invalid JSON blob and fall through to the raw-text fallback.
string ExtractJsonObject(string text)
{
    int start = text.IndexOf('{');
    if (start < 0)
    {
        return text;
    }

    int depth = 0;
    for (int i = start; i < text.Length; i++)
    {
        if (text[i] == '{')
        {
            depth++;
        }
        else if (text[i] == '}')
        {
            depth--;
            if (depth == 0)
            {
                return text.Substring(start, i - start + 1);
            }
        }
    }

    return text;
}

async Task<(string Text, string? ResponseId)> GetChatCompletionsAsync(string systemPrompt, string userPrompt, string? previousResponseId = null)
{
    var input = new List<object>();
    if (!string.IsNullOrWhiteSpace(systemPrompt))
    {
        // Resent every turn (not just the first) so dialect/behavior instructions don't get
        // diluted as the model's own prior turns accumulate in the conversation history
        input.Add(new { type = "message", role = "system", content = systemPrompt });
    }
    input.Add(new { type = "message", role = "user", content = userPrompt });

    var agentReference = new Dictionary<string, string>
    {
        ["name"] = foundryAgentName,
        ["type"] = "agent_reference"
    };
    if (!string.IsNullOrWhiteSpace(foundryAgentVersion))
    {
        agentReference["version"] = foundryAgentVersion;
    }

    var requestBody = new Dictionary<string, object>
    {
        ["input"] = input,
        ["agent_reference"] = agentReference,
        // "reasoning" is rejected outright when using agent_reference (model settings are
        // controlled by the agent definition itself, not per-request), so only cap length here
        ["max_output_tokens"] = 200
    };
    if (!string.IsNullOrWhiteSpace(previousResponseId))
    {
        requestBody["previous_response_id"] = previousResponseId;
    }

    var token = await foundryCredential.GetTokenAsync(
        new TokenRequestContext(new[] { "https://ai.azure.com/.default" }));

    using var request = new HttpRequestMessage(HttpMethod.Post, foundryResponsesUri)
    {
        Content = JsonContent.Create(requestBody)
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

    var response = await foundryHttpClient.SendAsync(request);
    var responseBody = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new HttpRequestException(
            $"Foundry Responses call failed with {(int)response.StatusCode} {response.StatusCode}: {responseBody}");
    }

    using var responseDocument = JsonDocument.Parse(responseBody);
    var root = responseDocument.RootElement;
    var responseId = root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : null;
    return (ExtractOutputText(root), responseId);
}

string ExtractOutputText(JsonElement root)
{
    if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
    {
        return outputText.GetString() ?? string.Empty;
    }

    var text = new StringBuilder();
    if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var partText) && partText.ValueKind == JsonValueKind.String)
                {
                    text.Append(partText.GetString());
                }
            }
        }
    }

    return text.ToString();
}

async Task HandleRecognizeAsync(CallMedia callConnectionMedia, string callerId, string message, PlaySource promptOverride = null)
{
    // Play greeting message
    var greetingPlaySource = promptOverride ?? new TextSource(message)
    {
        VoiceName = "en-US-NancyNeural"
    };

    var recognizeOptions =
        new CallMediaRecognizeSpeechOptions(
            targetParticipant: CommunicationIdentifier.FromRawId(callerId))
        {
            InterruptPrompt = false,
            InitialSilenceTimeout = TimeSpan.FromSeconds(15),
            Prompt = greetingPlaySource,
            SpeechLanguages = new List<string> { "ar-SA", "en-US" },
            OperationContext = "GetFreeFormText",
            EndSilenceTimeout = TimeSpan.FromMilliseconds(500)
        };

    var recognize_result = await callConnectionMedia.StartRecognizingAsync(recognizeOptions);
}

// Bilingual greeting: English then Saudi Arabic, each in its native TTS voice via SSML.
// The Arabic segment is wrapped in its own <lang> tag so it isn't normalized under the speak
// element's en-US locale (root xml:lang otherwise leaks into text-normalization/pronunciation
// for the whole document). Text is simple, clear Modern Standard Arabic — not slang — since
// that's what the ar-SA neural voice pronounces most naturally.
SsmlSource BuildBilingualGreetingSsml()
{
    string ssml = """
        <speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="en-US">
            <voice name="en-US-NancyNeural">Hello, thank you for calling! How can I help you today?</voice>
            <voice name="ar-SA-HamedNeural"><lang xml:lang="ar-SA">حياكم الله، كيف أقدر أخدمكم اليوم؟</lang></voice>
        </speak>
        """;
    return new SsmlSource(ssml);
}

async Task HandlePlayAsync(string textToPlay, string context, CallMedia callConnectionMedia, string voiceName = "en-US-NancyNeural", string sourceLocale = "en-US")
{
    // Play message
    var playSource = new TextSource(SanitizeForSpeech(textToPlay))
    {
        VoiceName = voiceName,
        SourceLocale = sourceLocale
    };

    var playOptions = new PlayToAllOptions(playSource) { OperationContext = context };
    await callConnectionMedia.PlayToAllAsync(playOptions);
}

app.Run();

class AgentReply
{
    public string Content { get; set; } = string.Empty;
    public int Score { get; set; } = -1;
    public string Intent { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Language { get; set; } = "en";
    public bool Escalate { get; set; }
    public bool EndCall { get; set; }
}
