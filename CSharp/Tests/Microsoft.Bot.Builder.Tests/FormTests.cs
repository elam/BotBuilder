﻿// 
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.
// 
// Microsoft Bot Framework: http://botframework.com
// 
// Bot Builder SDK Github:
// https://github.com/Microsoft/BotBuilder
// 
// Copyright (c) Microsoft Corporation
// All rights reserved.
// 
// MIT License:
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Connector;
using Microsoft.Bot.Builder.FormFlow;
using Microsoft.Bot.Builder.FormFlow.Advanced;
using Microsoft.Bot.Builder.FormFlow.Json;
using Microsoft.Bot.Builder.FormFlowTest;
using Microsoft.Bot.Builder.Dialogs.Internals;
using Microsoft.Bot.Builder.Internals.Fibers;
using Microsoft.Bot.Builder.Luis.Models;
using Microsoft.Bot.Sample.AnnotatedSandwichBot;

using Moq;
using Autofac;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.Bot.Builder.Tests
{
#pragma warning disable CS1998

    [TestClass]
    public sealed class FormTests : DialogTestBase
    {
        public async Task RecordScript(ILifetimeScope container,
            StreamWriter stream,
            Func<string> extraInfo,
            params string[] inputs)
        {
            var toBot = MakeTestMessage();
            using (var scope = DialogModule.BeginLifetimeScope(container, toBot))
            {
                var task = scope.Resolve<IPostToBot>();
                var queue = scope.Resolve<Queue<IMessageActivity>>();
                foreach (var input in inputs)
                {
                    stream.WriteLine($"FromUser:{JsonConvert.SerializeObject(input)}");
                    toBot.Text = input;
                    try
                    {
                        await task.PostAsync(toBot, CancellationToken.None);
                        stream.WriteLine($"{queue.Count()}");
                        while (queue.Count > 0)
                        {
                            var toUser = queue.Dequeue();
                            if (!string.IsNullOrEmpty(toUser.Text))
                            {
                                stream.WriteLine($"ToUserText:{JsonConvert.SerializeObject(toUser.Text)}");
                            }
                            else
                            {
                                stream.WriteLine($"ToUserButtons:{JsonConvert.SerializeObject(toUser.Attachments)}");
                            }
                        }
                        if (extraInfo != null)
                        {
                            var extra = extraInfo();
                            stream.WriteLine(extra);
                        }
                    }
                    catch (Exception e)
                    {
                        stream.WriteLine($"Exception:{e.Message}");
                    }
                }
            }
        }

        public string ReadLine(StreamReader stream, out string label)
        {
            string line = stream.ReadLine();
            label = null;
            if (line != null)
            {
                int pos = line.IndexOf(':');
                if (pos != -1)
                {
                    label = line.Substring(0, pos);
                    line = line.Substring(pos + 1);
                }
            }
            return line;
        }

        public async Task VerifyScript(ILifetimeScope container, StreamReader stream, Action<string> extraCheck, string[] expected)
        {
            var toBot = MakeTestMessage();
            using (var scope = DialogModule.BeginLifetimeScope(container, toBot))
            {
                var task = scope.Resolve<IPostToBot>();
                var queue = scope.Resolve<Queue<IMessageActivity>>();
                int current = 0;
                string input, label;
                while ((input = ReadLine(stream, out label)) != null)
                {
                    input = input.Substring(1, input.Length - 2);
                    Assert.IsTrue(current < expected.Length && input == expected[current++]);
                    toBot.Text = input;
                    try
                    {
                        await task.PostAsync(toBot, CancellationToken.None);
                        var count = int.Parse(stream.ReadLine());
                        Assert.AreEqual(count, queue.Count);
                        for (var i = 0; i < count; ++i)
                        {
                            var toUser = queue.Dequeue();
                            var expectedOut = ReadLine(stream, out label);
                            if (label == "ToUserText")
                            {
                                Assert.AreEqual(expectedOut, JsonConvert.SerializeObject(toUser.Text));
                            }
                            else
                            {
                                Assert.AreEqual(expectedOut, JsonConvert.SerializeObject(toUser.Attachments));
                            }
                        }
                        extraCheck?.Invoke(ReadLine(stream, out label));
                    }
                    catch (Exception e)
                    {
                        Assert.AreEqual(ReadLine(stream, out label), e.Message);
                    }
                }
            }
        }

        public async Task RecordFormScript<T>(string filePath,
            string locale, BuildFormDelegate<T> buildForm, FormOptions options, T initialState, IEnumerable<EntityRecommendation> entities,
            params string[] inputs)
            where T : class
        {
            using (var stream = new StreamWriter(filePath))
            using (var container = Build(Options.ResolveDialogFromContainer | Options.Reflection))
            {
                var root = new FormDialog<T>(initialState, buildForm, options, entities, CultureInfo.GetCultureInfo(locale));
                var builder = new ContainerBuilder();
                builder
                    .RegisterInstance(root)
                    .AsSelf()
                    .As<IDialog<object>>();
                builder.Update(container);
                stream.WriteLine($"{locale}");
                stream.WriteLine($"{JsonConvert.SerializeObject(initialState)}");
                stream.WriteLine($"{JsonConvert.SerializeObject(entities)}");
                await RecordScript(container, stream, () => "State:" + JsonConvert.SerializeObject(initialState), inputs);
            }
        }

        public async Task VerifyFormScript<T>(string filePath,
            string locale, BuildFormDelegate<T> buildForm, FormOptions options, T initialState, IEnumerable<EntityRecommendation> entities,
            params string[] inputs)
            where T : class
        {
            var newPath = Path.Combine(Path.GetDirectoryName(filePath), Path.GetFileNameWithoutExtension(filePath) + "-new" + Path.GetExtension(filePath));
            File.Delete(newPath);
            var currentState = JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(initialState));
            try
            {
                using (var stream = new StreamReader(filePath))
                using (var container = Build(Options.ResolveDialogFromContainer | Options.Reflection))
                {
                    var root = new FormDialog<T>(currentState, buildForm, options, entities, CultureInfo.GetCultureInfo(locale));
                    var builder = new ContainerBuilder();
                    builder
                        .RegisterInstance(root)
                        .AsSelf()
                        .As<IDialog<object>>();
                    builder.Update(container);
                    Assert.AreEqual(locale, stream.ReadLine());
                    Assert.AreEqual(JsonConvert.SerializeObject(initialState), stream.ReadLine());
                    Assert.AreEqual(JsonConvert.SerializeObject(entities), stream.ReadLine());
                    await VerifyScript(container, stream, (state) => Assert.AreEqual(state, JsonConvert.SerializeObject(currentState)), inputs);
                }
            }
            catch (Exception)
            {
                // There was an error, so record new script and pass on error
                await RecordFormScript(newPath, locale, buildForm, options, initialState, entities, inputs);
                throw;
            }
        }

        public interface IFormTarget
        {
            string Text { get; set; }
            int Integer { get; set; }
            float Float { get; set; }
        }

        private static class Input
        {
            public const string Text = "some text here";
            public const int Integer = 99;
            public const float Float = 1.5f;
        }

        [Serializable]
        private sealed class FormTarget : IFormTarget
        {
            float IFormTarget.Float { get; set; }
            int IFormTarget.Integer { get; set; }
            string IFormTarget.Text { get; set; }
        }

        public enum SimpleChoices { One = 1, Two, Three };

        [Serializable]
        private sealed class SimpleForm
        {
            public string Text { get; set; }
            public int Integer { get; set; }
            public float? Float { get; set; }
            public SimpleChoices Choices { get; set; }
            public DateTime Date { get; set; }
        }

        private static async Task RunScriptAgainstForm(IEnumerable<EntityRecommendation> entities, params string[] script)
        {
            IFormTarget target = new FormTarget();
            using (var container = Build(Options.ResolveDialogFromContainer, target))
            {
                {
                    var root = new FormDialog<IFormTarget>(target, entities: entities);
                    var builder = new ContainerBuilder();
                    builder
                        .RegisterInstance(root)
                        .AsSelf()
                        .As<IDialog<object>>();
                    builder.Update(container);
                }

                await AssertScriptAsync(container, script);
                {
                    Assert.AreEqual(Input.Text, target.Text);
                    Assert.AreEqual(Input.Integer, target.Integer);
                    Assert.AreEqual(Input.Float, target.Float);
                }
            }
        }

        [TestMethod]
        public async Task Simple_Form_Script()
        {
            await VerifyFormScript(@"..\..\SimpleForm.script",
                "en-us", () => new FormBuilder<SimpleForm>().AddRemainingFields().Build(), FormOptions.None, new SimpleForm(), Array.Empty<EntityRecommendation>(),
                "Hi",

                "?",
                "some text here",

                "?",
                "99",
                "back",
                "c",

                "?",
                "1.5",

                "?",
                "one",

                "help",
                "status",
                "1/1/2016"
                );
        }

        [TestMethod]
        public async Task Test_Next_Script()
        {
            await VerifyFormScript(@"..\..\SimpleForm-next.script",
                "en-us", () => new FormBuilder<SimpleForm>()
                    .Field(new FieldReflector<SimpleForm>("Text")
                        .SetNext((value, state) => new NextStep(new string[] { "Float" })))
                    .AddRemainingFields()
                    .Build(),
                FormOptions.None, new SimpleForm(), Array.Empty<EntityRecommendation>(),
                "Hi",
                "some text here",
                "1.5",
                "one",
                "1/1/2016",
                "99"
                );
        }

        [TestMethod]
        public async Task Pizza_Script()
        {
            await VerifyFormScript(@"..\..\PizzaForm.script",
                "en-us", () => PizzaOrder.BuildForm(), FormOptions.None, new PizzaOrder(), Array.Empty<EntityRecommendation>(),
                "hi",
                "garbage",
                "2",
                "med",
                "4",
                "help",
                "drink bread",
                "back",
                "c",
                "garbage",
                "no",
                "thin",
                "1",
                "?",
                "garbage",
                "beef, onion, ice cream",
                "garbage",
                "onions",
                "status",
                "abc",
                "2",
                "garbage",
                "iowa",
                "y",
                "1 2",
                "none",
                "garbage",
                "2.5",
                "garbage",
                "2/25/1962 3pm",
                "no",
                "1234",
                "123-4567",
                "no",
                "toppings",
                "everything but spinach",
                "y"
                );
        }

        [TestMethod]
        public async Task Pizza_Entities_Script()
        {
            await VerifyFormScript(@"..\..\PizzaForm-entities.script",
                "en-us", () => PizzaOrder.BuildForm(), FormOptions.None, new PizzaOrder(),
                new Luis.Models.EntityRecommendation[] {
                                new Luis.Models.EntityRecommendation("Address", "abc", "DeliveryAddress"),
                                new Luis.Models.EntityRecommendation("Kind", "byo", "Kind"),
                                // This should be skipped because it is not active
                                new Luis.Models.EntityRecommendation("Signature", "Hawaiian", "Signature"),
                                new Luis.Models.EntityRecommendation("Toppings", "onions", "BYO.Toppings"),
                                new Luis.Models.EntityRecommendation("Toppings", "peppers", "BYO.Toppings"),
                                new Luis.Models.EntityRecommendation("Toppings", "ice", "BYO.Toppings"),
                                new Luis.Models.EntityRecommendation("NotFound", "OK", "Notfound")
                            },
                "hi",
                "1", // onions for topping clarification
                "2", 
                "med", 
                // Kind "4",
                "drink bread",
                "thin",
                "1",
                "?",
                // "beef, onion, ice cream",
                "3",
                "y",
                "1 2",
                "none",
                "2.5",
                "2/25/1962 3pm",
                "no",
                "123-4567",
                "y"
                );
        }

        [TestMethod]
        public async Task Pizza_Button_Script()
        {
            await VerifyFormScript(@"..\..\PizzaFormButton.script",
                "en-us", () => PizzaOrder.BuildForm(style: ChoiceStyleOptions.Auto), FormOptions.None, new PizzaOrder(), Array.Empty<EntityRecommendation>(),
                "hi",
                "garbage",
                "2",
                "med",
                "4",
                "help",
                "drink bread",
                "back",
                "c",
                "garbage",
                "no",
                "thin",
                "1",
                "?",
                "garbage",
                "beef, onion, ice cream",
                "garbage",
                "onions",
                "status",
                "abc",
                "2",
                "garbage",
                "iowa",
                "y",
                "1 2",
                "none",
                "garbage",
                "2.5",
                "garbage",
                "2/25/1962 3pm",
                "no",
                "1234",
                "123-4567",
                "no",
                "toppings",
                "everything but spinach",
                "y"
                );
        }

        [TestMethod]
        public async Task Pizza_fr_Script()
        {
            await VerifyFormScript(@"..\..\PizzaForm-fr.script",
                "fr", () => PizzaOrder.BuildForm(), FormOptions.None, new PizzaOrder(), Array.Empty<EntityRecommendation>(),
                "bonjour",
                "2",
                "moyen",
                "4",
                "?",
                "1 2",
                "retourner",
                "c",
                "non",
                "fine",
                "1",
                "?",
                "bovine, oignons, ice cream",
                "oignons",
                "statut",
                "abc",
                "1 state street",
                "oui",
                "1 2",
                "non",
                "2,5",
                "25/2/1962 3pm",
                "non",
                "1234",
                "123-4567",
                "non",
                "nappages",
                "non epinards",
                "oui"
                );
        }

        [TestMethod]
        public async Task JSON_Script()
        {
            await VerifyFormScript(@"..\..\JSON.script",
                "en-us", () => SandwichOrder.BuildJsonForm(), FormOptions.None, new JObject(), Array.Empty<EntityRecommendation>(),
                "hi",
                "ham",
                "six",
                "nine grain",
                "wheat",
                "1",
                "peppers",
                "1",
                "2",
                "n",
                "no",
                "ok",
                "abc",
                "1 state st",
                "",
                "",
                "y",
                "2.5"
                );
        }

        [TestMethod]
        public async Task FormFlow_Localization()
        {
            // This ensures there are no bad templates in resources
            foreach (var locale in new string[] { "ar", "en", "es", "fa", "fr", "it", "ja", "ru", "zh-Hans" })
            {
                var root = new FormDialog<PizzaOrder>(new PizzaOrder(), () => PizzaOrder.BuildForm(), cultureInfo: CultureInfo.GetCultureInfo(locale));
                Assert.AreNotEqual(null, root);
            }
        }

        [TestMethod]
        public async Task Form_Can_Fill_In_Scalar_Types()
        {
            IEnumerable<EntityRecommendation> entities = Enumerable.Empty<EntityRecommendation>();
            await RunScriptAgainstForm(entities,
                    "hello",
                    "Please enter text ",
                    Input.Text,
                    "Please enter a number for integer (current choice: 0)",
                    Input.Integer.ToString(),
                    "Please enter a number for float (current choice: 0)",
                    Input.Float.ToString()
                );
        }

        [TestMethod]
        public async Task Form_Can_Handle_Luis_Entity()
        {
            IEnumerable<EntityRecommendation> entities = new[] { new EntityRecommendation(type: nameof(IFormTarget.Text), entity: Input.Text) };
            await RunScriptAgainstForm(entities,
                    "hello",
                    "Please enter a number for integer (current choice: 0)",
                    Input.Integer.ToString(),
                    "Please enter a number for float (current choice: 0)",
                    Input.Float.ToString()
                );
        }

        [TestMethod]
        public async Task Form_Can_Handle_Irrelevant_Luis_Entity()
        {
            IEnumerable<EntityRecommendation> entities = new[] { new EntityRecommendation(type: "some random entity", entity: Input.Text) };
            await RunScriptAgainstForm(entities,
                    "hello",
                    "Please enter text ",
                    Input.Text,
                    "Please enter a number for integer (current choice: 0)",
                    Input.Integer.ToString(),
                    "Please enter a number for float (current choice: 0)",
                    Input.Float.ToString()
                );
        }

        [TestMethod]
        public async Task CanResolveDynamicFormFromContainer()
        {
            // This test has two purposes.
            // 1. show that IFormDialog can be resolved from the container
            // 2. show that json schema forms can be dynamically generated based on the incoming message
            // You will likely find that the extensibility in IForm's callback methods may be sufficient enough for most scenarios.

            using (var container = Build(Options.ResolveDialogFromContainer))
            {
                var builder = new ContainerBuilder();

                // make a dynamic IForm model based on the incoming message
                builder
                    .Register(c =>
                    {
                        var message = c.Resolve<IMessageActivity>();

                        // use the user's name as the prompt
                        const string TEMPLATE_PREFIX =
                        @"
                        {
                          'type': 'object',
                          'properties': {
                            'name': {
                              'type': 'string',
                              'Prompt': { 'Patterns': [ '";

                        const string TEMPLATE_SUFFIX =
                        @"' ] },
                            }
                          }
                        }
                        ";

                        var text = TEMPLATE_PREFIX + message.From.Id + TEMPLATE_SUFFIX;
                        var schema = JObject.Parse(text);

                        return
                            new FormBuilderJson(schema)
                            .AddRemainingFields()
                            .Build();
                    })
                    .As<IForm<JObject>>()
                    // lifetime must match lifetime scope tag of Message, since we're dependent on the Message
                    .InstancePerMatchingLifetimeScope(DialogModule.LifetimeScopeTag);

                builder
                    .Register<BuildFormDelegate<JObject>>(c =>
                    {
                        var cc = c.Resolve<IComponentContext>();
                        return () => cc.Resolve<IForm<JObject>>();
                    })
                    // tell the serialization framework to recover this delegate from the container
                    // rather than trying to serialize it with the dialog
                    // normally, this delegate is a static method that is trivially serializable without any risk of a closure capturing the environment
                    .Keyed<BuildFormDelegate<JObject>>(FiberModule.Key_DoNotSerialize)
                    .AsSelf()
                    .InstancePerMatchingLifetimeScope(DialogModule.LifetimeScopeTag);

                builder
                    .RegisterType<FormDialog<JObject>>()
                    // root dialog is an IDialog<object>
                    .As<IDialog<object>>()
                    .InstancePerMatchingLifetimeScope(DialogModule.LifetimeScopeTag);

                builder
                    // our default form state
                    .Register<JObject>(c => new JObject())
                    .AsSelf()
                    .InstancePerDependency();

                builder.Update(container);

                // verify that the form dialog prompt is dynamically generated from the incoming message
                await AssertScriptAsync(container,
                    "hello",
                    ChannelID.User
                    );
            }
        }
    }
}
