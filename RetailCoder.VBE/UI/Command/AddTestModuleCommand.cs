using System.Linq;
using System.Runtime.InteropServices;
using NLog;
using Rubberduck.Parsing.VBA;
using Rubberduck.Settings;
using Rubberduck.UnitTesting;
using Rubberduck.VBEditor.Extensions;
using Rubberduck.VBEditor.SafeComWrappers;
using Rubberduck.VBEditor.SafeComWrappers.Abstract;
using Rubberduck.VBEditor.SafeComWrappers.VBA;

namespace Rubberduck.UI.Command
{
    /// <summary>
    /// A command that adds a new test module to the active VBAProject.
    /// </summary>
    [ComVisible(false)]
    public class AddTestModuleCommand : CommandBase
    {
        private readonly IVBE _vbe;
        private readonly RubberduckParserState _state;
        private readonly IGeneralConfigService _configLoader;

        public AddTestModuleCommand(IVBE vbe, RubberduckParserState state, IGeneralConfigService configLoader)
            : base(LogManager.GetCurrentClassLogger())
        {
            _vbe = vbe;
            _state = state;
            _configLoader = configLoader;
        }

        private const string TestModuleEmptyTemplate = "'@TestModule\r\n{0}\r\n{1}\r\n{2}\r\n\r\n";
        private const string FolderAnnotation = "'@Folder(\"Tests\")\r\n";

        private const string FakesFieldDeclarationFormat = "Private Fakes As {0}";
        private const string AssertFieldDeclarationFormat = "Private Assert As {0}";

        private readonly string _moduleInit = string.Concat(
            "'@ModuleInitialize\r\n",
            "Public Sub ModuleInitialize()\r\n",
            $"    '{RubberduckUI.UnitTest_NewModule_RunOnce}.\r\n",
            "    {0}\r\n",
            "    {1}\r\n",
            "End Sub\r\n\r\n",
            "'@ModuleCleanup\r\n",
            "Public Sub ModuleCleanup()\r\n",
            $"    '{RubberduckUI.UnitTest_NewModule_RunOnce}.\r\n",
            "    Set Assert = Nothing\r\n",
            "    Set Fakes = nothing\r\n",
            "End Sub\r\n\r\n"
        );

        private readonly string _methodInit = string.Concat(
            "'@TestInitialize\r\n"
            , "Public Sub TestInitialize()\r\n"
            , "    '", RubberduckUI.UnitTest_NewModule_RunBeforeTest, ".\r\n"
            , "End Sub\r\n\r\n"
            , "'@TestCleanup\r\n"
            , "Public Sub TestCleanup()\r\n"
            , "    '", RubberduckUI.UnitTest_NewModule_RunAfterTest, ".\r\n"
            , "End Sub\r\n\r\n"
        );

        private const string TestModuleBaseName = "TestModule";

        private string GetTestModule(IUnitTestSettings settings)
        {
            var assertType = string.Format("Rubberduck.{0}AssertClass", settings.AssertMode == AssertMode.StrictAssert ? string.Empty : "Permissive");
            var assertDeclaredAs = DeclarationFormatFor(AssertFieldDeclarationFormat, assertType, settings);

            var fakesType = "Rubberduck.IFake";
            var fakesDeclaredAs = DeclarationFormatFor(FakesFieldDeclarationFormat, fakesType, settings); 

            var formattedModuleTemplate = string.Format(TestModuleEmptyTemplate, FolderAnnotation, assertDeclaredAs, fakesDeclaredAs);

            if (settings.ModuleInit)
            {
                var assertBinding = InstantiationFormatFor(assertType, settings);
                var assertSetAs = $"Set Assert = {assertBinding}";

                var fakesBinding = InstantiationFormatFor(fakesType, settings);
                var fakesSetAs = $"Set Fakes = {fakesBinding}";

                formattedModuleTemplate += string.Format(_moduleInit, assertSetAs, fakesSetAs);
            }

            if (settings.MethodInit)
            {
                formattedModuleTemplate += _methodInit;
            }

            return formattedModuleTemplate;
        }

        private string InstantiationFormatFor(string type, IUnitTestSettings settings) 
        {
            const string EarlyBoundInstantiationFormat = "New {0}";
            const string LateBoundInstantiationFormat = "CreateObject(\"{0}\")";
            return string.Format(settings.BindingMode == BindingMode.EarlyBinding ? EarlyBoundInstantiationFormat : LateBoundInstantiationFormat, type); 
        }

        private string DeclarationFormatFor(string declarationFormat, string type, IUnitTestSettings settings) 
        {
            return string.Format(declarationFormat, settings.BindingMode == BindingMode.EarlyBinding ? type : "Object");
        }

        private IVBProject GetProject()
        {
            var activeProject = _vbe.ActiveVBProject;
            if (!activeProject.IsWrappingNullReference)
            {
                return activeProject;
            }

            var projects = _vbe.VBProjects;
            {
                return projects.Count == 1 
                    ? projects[1]
                    : new VBProject(null);
            }
        }

        protected override bool CanExecuteImpl(object parameter)
        {
            return !GetProject().IsWrappingNullReference && _vbe.HostSupportsUnitTests();
        }

        protected override void ExecuteImpl(object parameter)
        {
            var project = parameter as IVBProject ?? GetProject();
            if (project.IsWrappingNullReference)
            {
                return;
            }

            var settings = _configLoader.LoadConfiguration().UserSettings.UnitTestSettings;

            if (settings.BindingMode == BindingMode.EarlyBinding)
            {
                project.EnsureReferenceToAddInLibrary();
            }

            var component = project.VBComponents.Add(ComponentType.StandardModule);
            var module = component.CodeModule;
            component.Name = GetNextTestModuleName(project);

            var hasOptionExplicit = false;
            if (module.CountOfLines > 0 && module.CountOfDeclarationLines > 0)
            {
                hasOptionExplicit = module.GetLines(1, module.CountOfDeclarationLines).Contains("Option Explicit");
            }

            var options = string.Concat(hasOptionExplicit ? string.Empty : "Option Explicit\r\n",
                "Option Private Module\r\n\r\n");

            var defaultTestMethod = string.Empty;
            if (settings.DefaultTestStubInNewModule)
            {
                defaultTestMethod = AddTestMethodCommand.TestMethodTemplate.Replace(
                    AddTestMethodCommand.NamePlaceholder, "TestMethod1");
            }

            module.AddFromString(options + GetTestModule(settings) + defaultTestMethod);
            component.Activate();
            _state.OnParseRequested(this, component);
        }

        private string GetNextTestModuleName(IVBProject project)
        {
            var names = project.ComponentNames();
            var index = names.Count(n => n.StartsWith(TestModuleBaseName)) + 1;

            return string.Concat(TestModuleBaseName, index);
        }
    }
}
