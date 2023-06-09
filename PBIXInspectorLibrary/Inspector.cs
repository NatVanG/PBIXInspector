﻿using Json.Logic;
using Json.More;
using Json.Pointer;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PBIXInspectorLibrary.Utils;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace PBIXInspectorLibrary
{
    /// <summary>
    /// Iterates through input rules and runs then against input PBIX files
    /// </summary>
    public class Inspector : InspectorBase
    {
        private const string SUBJPATHSTART = "{";
        private const string SUBJPATHEND = "}";
        private const string FILTEREXPRESSIONMARKER = "?";
        private const string JSONPOINTERSTART = "/";
        private const string CONTEXTARRAY = ".";

        private string _pbiFilePath, _rulesFilePath;
        //private PbiFile _pbiFile;
        private InspectionRules? _inspectionRules;

        public event EventHandler<MessageIssuedEventArgs>? MessageIssued;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="pbiFilePath"></param>
        /// <param name="inspectionRules"></param>
        public Inspector(string pbiFilePath, InspectionRules inspectionRules) : base(pbiFilePath, inspectionRules)
        {
            this._pbiFilePath = pbiFilePath;
            this._inspectionRules = inspectionRules;
            AddCustomRulesToRegistry();
        }
        
        /// <summary>
        /// 
        /// </summary>
        /// <param name="pbiFilePath">Local PBIX file path</param>
        /// <param name="rulesFilePath">Local rules json file path</param>
        public Inspector(string pbiFilePath, string rulesFilePath) : base(pbiFilePath, rulesFilePath)
        {
            this._pbiFilePath = pbiFilePath;
            this._rulesFilePath = rulesFilePath;
            
            try
            {
                this._inspectionRules = this.DeserialiseRules<InspectionRules>(rulesFilePath);
            }
            catch (System.IO.FileNotFoundException e)
            {
                throw new PBIXInspectorException(string.Format("Rules file with path \"{0}\" not found.", rulesFilePath), e);
            }

            AddCustomRulesToRegistry();
        }

        private PbiFile InitPbiFile(string pbiFilePath)
        {
            switch (PbiFile.PBIFileType(pbiFilePath))
            {
                case PbiFile.PBIFileTypeEnum.PBIX:
                    return new PbixFile(pbiFilePath);
                    break;
                case PbiFile.PBIFileTypeEnum.PBIP:
                    return new PbipFile(pbiFilePath);
                    break;
                default:
                    return null;
            }
        }

        private void AddCustomRulesToRegistry()
        {
            //TODO: Use reflection to add rules
            /*
            System.Reflection.Assembly assembly = Assembly.GetExecutingAssembly();
            string nspace = "PBIXInspectorLibrary.CustomRules";

            var q = from t in Assembly.GetExecutingAssembly().GetTypes()
                    where t.IsClass && t.Namespace == nspace
                    select t;
            q.ToList().ForEach(t1 => Json.Logic.RuleRegistry.AddRule<t1>());
            */

            Json.Logic.RuleRegistry.AddRule<CustomRules.CountRule>();
            Json.Logic.RuleRegistry.AddRule<CustomRules.IsNullOrEmptyRule>();
            Json.Logic.RuleRegistry.AddRule<CustomRules.StringContains>();
            Json.Logic.RuleRegistry.AddRule<CustomRules.ToString>();
        }

        /// <summary>
        /// Core method
        /// </summary>
        public IEnumerable<TestResult> Inspect()
        {
            using (var pbiFile = InitPbiFile(_pbiFilePath))
            {
                if (pbiFile == null || this._inspectionRules == null)
                {
                    OnMessageIssued(MessageTypeEnum.Error, string.Format("Cannot start inspection as either the PBIX file or the Inspection Rules are not instantiated."));
                }
                else
                {
                    foreach (var entry in this._inspectionRules.PbiEntries)
                    {
                        var pbiEntryPath = pbiFile.FileType == PbiFile.PBIFileTypeEnum.PBIX ? entry.PbixEntryPath : entry.PbipEntryPath;
                        using (Stream entryStream = pbiFile.GetEntryStream(pbiEntryPath))
                        {
                            if (entryStream == null)
                            {
                                OnMessageIssued(MessageTypeEnum.Error, string.Format("PBI entry \"{0}\" with path \"{1}\" is not valid or does not exist, resuming to next PBIX Entry iteration if any.", entry.Name, entry.PbixEntryPath));
                                continue;
                            }

                            //TODO: retrieve PBIP encoding from codepage to allow for different encoding for PBIX and PBIP
                            Encoding encoding = pbiFile.FileType == PbiFile.PBIFileTypeEnum.PBIX ? GetEncodingFromCodePage(entry.CodePage) : Encoding.UTF8;

                            using StreamReader sr = new(entryStream, encoding);

                            //TODO: use TryParse instead but better still use stringenumconverter upon deserialising
                            EntryContentTypeEnum contentType;
                            if (!Enum.TryParse(entry.ContentType, out contentType))
                            {
                                throw new PBIXInspectorException(string.Format("ContentType \"{0}\" defined for entry \"{1}\" is not valid.", entry.ContentType, entry.Name));
                            }

                            switch (contentType)
                            {
                                case EntryContentTypeEnum.json:
                                    {
                                        using JsonTextReader reader = new(sr);

                                        var jo = (JObject)JToken.ReadFrom(reader);

                                        OnMessageIssued(MessageTypeEnum.Information, string.Format("Running rules for PBI entry \"{0}\"...", entry.Name));
                                        foreach (var rule in entry.Rules)
                                        {
                                            OnMessageIssued(MessageTypeEnum.Information, string.Format("Running Rule \"{0}\".", rule.Name));
                                            Json.Logic.Rule? jrule = null;

                                            try
                                            {
                                                jrule = System.Text.Json.JsonSerializer.Deserialize<Json.Logic.Rule>(rule.Test.Logic);
                                            }
                                            catch (System.Text.Json.JsonException e)
                                            {
                                                throw new PBIXInspectorException(string.Format("Parsing of logic for rule \"{0}\" failed.", rule.Name), e);
                                            }

                                            //Check if there's a foreach iterator
                                            if (rule != null && !string.IsNullOrEmpty(rule.ForEachPath))
                                            {
                                                var forEachTokens = ExecutePath(jo, rule.Name, rule.ForEachPath, rule.PathErrorWhenNoMatch);
                                                

                                                foreach (var forEachToken in forEachTokens)
                                                {
                                                    var tokens = ExecutePath((JObject?)forEachToken, rule.Name, rule.Path, rule.PathErrorWhenNoMatch);


                                                    var forEachName = !string.IsNullOrEmpty(rule.ForEachPathDisplayName) ? ExecutePath((JObject?)forEachToken, rule.Name, rule.ForEachPathDisplayName, rule.PathErrorWhenNoMatch) : null;
                                                    var strForEachName = forEachName != null ? forEachName[0].ToString() : string.Empty;

                                                    //HACK: I don't like it.
                                                    var contextNodeArray = ConvertToJsonArray(tokens);

                                                    //foreach (var node in contextNodeArray)
                                                    //{
                                                        bool result = false;

                                                        var newdata = MapRuleDataPointersToValues(contextNodeArray, rule, contextNodeArray);

                                                        //TODO: the following commented line does not work with the variableRule implementation with context array passed in.
                                                        //var jruleresult = jrule.Apply(newdata, contextNodeArray);
                                                        var jruleresult = jrule.Apply(newdata);
                                                        result = rule.Test.Expected.IsEquivalentTo(jruleresult);
                                                       
                                                        string resultString = string.Concat("\"",strForEachName, "\" - ", string.Format("Rule \"{0}\" {1} with result: {2}, expected: {3}.",  rule != null ? rule.Name : string.Empty, result ? "PASSED" : "FAILED", jruleresult != null ? jruleresult.ToString() : string.Empty, rule.Test.Expected != null ? rule.Test.Expected.ToString() : string.Empty));
#if DEBUG
                                                        //resultString = string.Concat(strForEachName, string.Format("Rule \"{0}\" {1} with result: {2} and data: {3}.", rule != null ? rule.Name : string.Empty, result ? "PASSED" : "FAILED", jruleresult != null ? jruleresult.ToString() : string.Empty, newdata.AsJsonString().Length > 1000 ? string.Concat(newdata.AsJsonString().Substring(0, 999), "[first 1000 characters]") : newdata.AsJsonString()));
#endif

                                                    //TODO: return jruleresult in TestResult so that we can compose test from other tests.

                                                    yield return new TestResult { Name = rule.Name, Result = result, ResultMessage = resultString };
                                                    //}
                                                }
                                            }
                                            else
                                            {
                                                var tokens = ExecutePath(jo, rule.Name, rule.Path, rule.PathErrorWhenNoMatch);

                                                //HACK: I don't like it.
                                                var contextNodeArray = ConvertToJsonArray(tokens);

                                                foreach (var node in contextNodeArray)
                                                {
                                                    bool result = false;

                                                    var newdata = MapRuleDataPointersToValues(node, rule, contextNodeArray);

                                                    //TODO: the following commented line does not work with the variableRule implementation with context array passed in.
                                                    //var jruleresult = jrule.Apply(newdata, contextNodeArray);
                                                    var jruleresult = jrule.Apply(newdata);
                                                    result = rule.Test.Expected.IsEquivalentTo(jruleresult);
                                                    string resultString = string.Format("Rule \"{0}\" {1} with result: {2}, expected: {3}.", rule != null ? rule.Name : string.Empty, result ? "PASSED" : "FAILED", jruleresult != null ? jruleresult.ToString() : string.Empty, rule.Test.Expected != null ? rule.Test.Expected.ToString() : string.Empty);

#if DEBUG
                                                    //resultString = string.Format("Rule \"{0}\" {1} with result: {2} and data: {3}.", rule != null ? rule.Name : string.Empty, result ? "PASSED" : "FAILED", jruleresult != null ? jruleresult.ToString() : string.Empty, newdata.AsJsonString().Length > 1000 ? string.Concat(newdata.AsJsonString().Substring(0, 999), "[first 1000 characters]") : newdata.AsJsonString());
#endif

                                                    //TODO: return jruleresult in TestResult so that we can compose test from other tests.
                                                    yield return new TestResult { Name = rule.Name, Result = result, ResultMessage = resultString };
                                                }
                                            }
                                            
                                        }
                                        break;
                                    }
                                case EntryContentTypeEnum.text:
                                    {
                                        throw new PBIXInspectorException("PBI entries with text content are not currently supported.");
                                    }
                                default:
                                    {
                                        throw new PBIXInspectorException("Only Json PBI entries are supported.");
                                    }
                            }
                        }
                    }
                }
            }
        }

        private JsonArray ConvertToJsonArray(List<JToken>? tokens)
        {
            List<JsonNode>? nodes = new();

            if (tokens != null)
            {
                foreach (var token in tokens)
                {
                    JsonNode? node;

                    try
                    {
                        node = JsonNode.Parse(token.ToString());
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        node = token.ToString();
                    }

                    if (node != null) nodes.Add(node);
                }
            }

            return new JsonArray(nodes.ToArray());
        }

        /// <summary>
        /// Execute JPaths and sub JPaths
        /// </summary>
        /// <param name="jo"></param>
        /// <param name="rule"></param>
        /// <returns></returns>
        private List<JToken>? ExecutePath(JObject? jo, string ruleName, string rulePath, bool rulePathErrorWhenNoMatch)
        {
            string parentPath, queryPath = string.Empty;
            List<JToken>? tokens = new List<JToken>();

            //TODO: a regex match to extract the substring would be better
            if (rulePath.Contains(SUBJPATHSTART)) //check for subpath syntax
            {
                if (rulePath.EndsWith(SUBJPATHEND))
                {
                    //TODO: currently subpath is assumed to be the last path (i.e. the whole string end in "})" but we should be able to resolve inner subpath and return to parent path
                    var index = rulePath.IndexOf(SUBJPATHSTART);
                    parentPath = rulePath.Substring(0, index);
                    queryPath = rulePath.Substring(index + SUBJPATHSTART.Length, rulePath.Length - (index + SUBJPATHSTART.Length) - 1);
                    var parentTokens = SelectTokens(jo, ruleName, parentPath, rulePathErrorWhenNoMatch);

                    if (parentPath.Contains(FILTEREXPRESSIONMARKER))
                    {
                        JArray ja = new JArray();
                        foreach (var t in parentTokens)
                        {
                            //HACK: why do I have to parse a token into a token to make the subsequent SelectTokens call work?
                            var jt = JToken.Parse(t.ToString());
                            ja.Add(jt);
                        }

                        tokens = ja.SelectTokens(queryPath, rulePathErrorWhenNoMatch)?.ToList();
                    }
                    else
                    {
                        foreach (var t in parentTokens)
                        {
                            //var childtokens = SelectTokens((JObject?)t, rule.Name, childPath, rule.PathErrorWhenNoMatch); //TODO: this seems better but throws InvalidCastException
                            var childtokens = SelectTokens(((JObject)JToken.Parse(t.ToString())), ruleName, queryPath, rulePathErrorWhenNoMatch);
                            //only return children tokens, the reference to parent tokens is lost. 
                            if (childtokens != null) tokens.AddRange(childtokens.ToList());
                        }
                    }
                }
                else
                {
                    throw new PBIXInspectorException(string.Format("Path \"{0}\" needs to end with \"{1}\" as it contains a subpath.", rulePath, "}"));
                }
            }
            else
            {
                tokens = SelectTokens(jo, ruleName, rulePath, rulePathErrorWhenNoMatch)?.ToList();
            }

            return tokens;
        }

        //private IEnumerable<JToken>? SelectTokens(JObject? jo, Rule rule)
        //{
        //    return SelectTokens(jo, rule.Name, rule.Path, rule.PathErrorWhenNoMatch);
        //}

        private IEnumerable<JToken>? SelectTokens(JObject? jo, string ruleName, string rulePath, bool rulePathErrorWhenNoMatch)
        {
            IEnumerable<JToken>? tokens;

            //Faster without a Try catch block hence the conditional branching
            if (!rulePathErrorWhenNoMatch)
            {
                tokens = jo.SelectTokens(rulePath, false);
            }
            else
            {
                //TODO: for some reason I can't catch Newtonsoft.Json.JsonException when rule.PathErrorWhenNoMatch is true
                tokens = jo.SelectTokens(rulePath, false);
                if (tokens == null || tokens.Count() == 0)
                {
                    OnMessageIssued(MessageTypeEnum.Information, string.Format("Rule \"{0}\" with JPath \"{1}\" did not return any tokens.", ruleName, rulePath));
                }

                //try
                //{
                //    tokens = jo.SelectTokens(path, true);
                //}
                //catch (Newtonsoft.Json.JsonException e)
                //{
                //    Console.WriteLine("ERROR: {0}", e.Message);
                //}
                //catch
                //{
                //    Console.WriteLine("ERROR: Path \"{0}\" not found.");
                //}
            }

            return tokens;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="token"></param>
        /// <param name="rule"></param>
        /// <returns></returns>
        private JsonNode? MapRuleDataPointersToValues(JsonNode? target, Rule rule, JsonArray? contextNodeArray)
        {
            if (rule.Test.Data == null || rule.Test.Data is not JsonObject) return rule.Test.Data;

            var newdata = new JsonObject();

            var olddata = rule.Test.Data.AsObject();

            if (target != null)
            {
                try
                {
                    if (olddata != null && olddata.Count() > 0)
                    {
                        foreach (var item in olddata)
                        {
                            if (item.Value is JsonValue)
                            {
                                
                                var value = item.Value.AsValue().Stringify();
                                //TODO: enable navigation to parent path
                                //while (value.StartsWith("."))
                                //{
                                //    target = target.Parent;
                                //    value = value.Substring(1, value.Length - 1);
                                //}

                                if (value.StartsWith(JSONPOINTERSTART)) //check for JsonPointer syntax
                                {
                                    try
                                    {
                                        var pointer = JsonPointer.Parse(value);
                                        var evalsuccess = pointer.TryEvaluate(target, out var newval);
                                        if (evalsuccess)
                                        {
                                            if (newval != null)
                                            {
                                                newdata.Add(new KeyValuePair<string, JsonNode?>(item.Key, newval.Copy()));
                                            }
                                            else
                                            {
                                                //TODO: handle null value?
                                            }
                                        }
                                        else
                                        {
                                            if (rule.PathErrorWhenNoMatch)
                                            {
                                                throw new PBIXInspectorException(string.Format("Rule \"{0}\" - Could not evaluate json pointer \"{1}\"", rule.Name, value));
                                            }
                                        }
                                    }
                                    catch (PointerParseException e)
                                    {
                                        throw new PBIXInspectorException(string.Format("Rule \"{0}\" - Pointer exception for value \"{1}\"", rule.Name, value), e);
                                    }
                                }
                                else if (value.StartsWith(CONTEXTARRAY))
                                {
                                    //context array token was used so pass in the parent array
                                    newdata.Add(new KeyValuePair<string, JsonNode?>(item.Key, contextNodeArray.Copy()));
                                }
                                else
                                {
                                    //looks like a literal value
                                    newdata.Add(new KeyValuePair<string, JsonNode?>(item.Key, item.Value.Copy()));
                                }
                            }
                            else
                            {
                                //might be a JsonArray
                                newdata.Add(new KeyValuePair<string, JsonNode?>(item.Key, item.Value.Copy()));
                            }
                        }
                    }
                }
                catch (System.Text.Json.JsonException e)
                {
                    throw new PBIXInspectorException("JsonException", e);
                }

            }

            return newdata;
        }


        private Encoding GetEncodingFromCodePage(int codePage)
        {
            var enc = Encoding.Unicode;

            try
            {
                enc = Encoding.GetEncoding(codePage);
            }
            catch
            {
                OnMessageIssued(MessageTypeEnum.Information, string.Format("Encoding code page value {0} for PBIX entry is not valid, defaulting to {1}.", codePage, enc.EncodingName));
            }

            return enc;
        }

        protected void OnMessageIssued(MessageTypeEnum messageType, string message)
        {
            var args = new MessageIssuedEventArgs { MessageType = messageType, Message = message };
            OnMessageIssued(args);
        }

        protected virtual void OnMessageIssued(MessageIssuedEventArgs e)
        {
            EventHandler<MessageIssuedEventArgs>? handler = MessageIssued;
            if (handler != null)
            {
                handler(this, e);
            }
        }
    }
}
