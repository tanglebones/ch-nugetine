using System;
using Extensibility;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.CommandBars;

/**
 * To make this work you need to copy NugetFix.Addin and NugetFix.dll to your visual studio /Addin folder.
 * e.g. ~\Documents\Visual Studio 2010\Addins
 * 
 * For now the end result is a menu command (NugetFix) under "Red"
 */
namespace NugetFix
{
	/// <summary>The object for implementing an Add-in.</summary>
	/// <seealso class='IDTExtensibility2' />
	public class Connect : IDTExtensibility2, IDTCommandTarget
	{
        private DTE2 _applicationObject;
        private AddIn _addInInstance;
	    private readonly NugetFixInternal _nugetFixInternal = new NugetFixInternal();
	    private const int RedPopupId = 738492022;
	    private const string CommandNameConst = "NugetFix.Connect.NugetFix";

        // icon selected from this list:
        // http://www.kebabshopblues.co.uk/2007/01/04/visual-studio-2005-tools-for-office-commandbarbutton-faceid-property/
        public const int IconBitmapId= 93; 

	    /// <summary>Implements the OnConnection method of the IDTExtensibility2 interface. Receives notification that the Add-in is being loaded.</summary>
	    /// <param term='application'>Root object of the host application.</param>
	    /// <param term='connectMode'>Describes how the Add-in is being loaded.</param>
	    /// <param term='addInInst'>Object representing this Add-in.</param>
	    /// <param name="application"> </param>
	    /// <param name="connectMode"> </param>
	    /// <param name="addInInst"> </param>
	    /// <param name="custom"> </param>
	    /// <seealso class='IDTExtensibility2' />
	    public void OnConnection(object application, ext_ConnectMode connectMode, object addInInst, ref Array custom)
		{
			_applicationObject = (DTE2)application;
	        _nugetFixInternal.SetApplicationObject(_applicationObject);
			_addInInstance = (AddIn)addInInst;

			var contextGuids = new object[] { };
			var commands = (Commands2) _applicationObject.Commands;
			const string redMenuName = "Red";

			//Find the MenuBar command bar, which is the top-level command bar holding all the main menu items:
			var menuBarCommandBar = ((CommandBars)_applicationObject.CommandBars)["MenuBar"];

			//Find the Tools command bar on the MenuBar command bar so we can add our menu after it:
            var toolsControl = menuBarCommandBar.Controls["Tools"];
			CommandBarControl redMenu;
            try
            {
                redMenu = menuBarCommandBar.Controls[redMenuName];
            }
            catch (Exception)
            {
                redMenu = menuBarCommandBar.Controls.Add(MsoControlType.msoControlPopup, RedPopupId, Before: toolsControl.Index + 1);
                redMenu.Caption = redMenuName;
            }
			var redPopup = (CommandBarPopup) redMenu;

            // ReSharper disable RedundantArgumentDefaultValue
            // ReSharper disable RedundantArgumentName

			//This try/catch block can be duplicated if you wish to add multiple commands to be handled by your Add-in,
			//  just make sure you also update the QueryStatus/Exec method to include the new command names.
	        Command command = null;
			try
			{
				//Add a command to the Commands collection:
				command = commands.AddNamedCommand2(_addInInstance, "NugetFix", "NugetFix", "Executes the command for NugetFix", true, IconBitmapId, 
                    ref contextGuids,
                    vsCommandStatusValue: (int)vsCommandStatus.vsCommandStatusSupported 
                                            + (int)vsCommandStatus.vsCommandStatusEnabled, 
				    CommandStyleFlags: (int)vsCommandStyle.vsCommandStylePictAndText, 
				    ControlType: vsCommandControlType.vsCommandControlTypeButton);
			}
			catch(ArgumentException ae)
			{
				//If we are here, then the exception is probably because a command with that name
				//  already exists. If so there is no need to recreate the command and we can 
                //  safely ignore the exception.
                //Add a control for the command to the tools menu:
                _nugetFixInternal.Print(ae.Message);
                try
                {
                    command = commands.Item(CommandNameConst);
                } catch (Exception e)
                {
                    _nugetFixInternal.Print(e.Message);
                }
			}
            finally
			{
                //Add a control for the command to the tools menu:
                if ((command != null) && (redPopup != null))
                {
                    command.AddControl(redPopup.CommandBar, Position: 1);
                }
			}
		}

	    /// <summary>Implements the OnDisconnection method of the IDTExtensibility2 interface. Receives notification that the Add-in is being unloaded.</summary>
	    /// <param term='disconnectMode'>Describes how the Add-in is being unloaded.</param>
	    /// <param term='custom'>Array of parameters that are host application specific.</param>
	    /// <param name="disconnectMode"> </param>
	    /// <param name="custom"> </param>
	    /// <seealso class='IDTExtensibility2' />
	    public void OnDisconnection(ext_DisconnectMode disconnectMode, ref Array custom)
		{
		}

	    /// <summary>Implements the OnAddInsUpdate method of the IDTExtensibility2 interface. Receives notification when the collection of Add-ins has changed.</summary>
	    /// <param term='custom'>Array of parameters that are host application specific.</param>
	    /// <param name="custom"> </param>
	    /// <seealso class='IDTExtensibility2' />		
	    public void OnAddInsUpdate(ref Array custom)
		{
		}

	    /// <summary>Implements the OnStartupComplete method of the IDTExtensibility2 interface. Receives notification that the host application has completed loading.</summary>
	    /// <param term='custom'>Array of parameters that are host application specific.</param>
	    /// <param name="custom"> </param>
	    /// <seealso class='IDTExtensibility2' />
	    public void OnStartupComplete(ref Array custom)
		{
		}

	    /// <summary>Implements the OnBeginShutdown method of the IDTExtensibility2 interface. Receives notification that the host application is being unloaded.</summary>
	    /// <param term='custom'>Array of parameters that are host application specific.</param>
	    /// <param name="custom"> </param>
	    /// <seealso class='IDTExtensibility2' />
	    public void OnBeginShutdown(ref Array custom)
		{
		}

        // ReSharper disable RedundantCast
        // ReSharper disable BitwiseOperatorOnEnumWithoutFlags

	    /// <summary>Implements the QueryStatus method of the IDTCommandTarget interface. This is called when the command's availability is updated</summary>
	    /// <param term='commandName'>The name of the command to determine state for.</param>
	    /// <param term='neededText'>Text that is needed for the command.</param>
	    /// <param term='status'>The state of the command in the user interface.</param>
	    /// <param term='commandText'>Text requested by the neededText parameter.</param>
	    /// <param name="commandName"> </param>
	    /// <param name="neededText"> </param>
	    /// <param name="status"> </param>
	    /// <param name="commandText"> </param>
	    /// <seealso class='Exec' />
	    public void QueryStatus(string commandName, vsCommandStatusTextWanted neededText, ref vsCommandStatus status, ref object commandText)
		{
			if(neededText == vsCommandStatusTextWanted.vsCommandStatusTextWantedNone)
			{
                if (commandName == CommandNameConst)
				{
					status = (vsCommandStatus)vsCommandStatus.vsCommandStatusSupported|vsCommandStatus.vsCommandStatusEnabled;
				}
			}
		}

        // ReSharper restore BitwiseOperatorOnEnumWithoutFlags
        // ReSharper restore RedundantCast

	    /// <summary>Implements the Exec method of the IDTCommandTarget interface. This is called when the command is invoked.</summary>
	    /// <param term='commandName'>The name of the command to execute.</param>
	    /// <param term='executeOption'>Describes how the command should be run.</param>
	    /// <param term='varIn'>Parameters passed from the caller to the command handler.</param>
	    /// <param term='varOut'>Parameters passed from the command handler to the caller.</param>
	    /// <param term='handled'>Informs the caller if the command was handled or not.</param>
	    /// <param name="commandName"> </param>
	    /// <param name="executeOption"> </param>
	    /// <param name="varIn"> </param>
	    /// <param name="varOut"> </param>
	    /// <param name="handled"> </param>
	    /// <seealso class='Exec' />
        // ReSharper disable RedundantAssignment
	    public void Exec(string commandName, vsCommandExecOption executeOption, ref object varIn, ref object varOut, ref bool handled)
		{
			handled = false;
			if(executeOption == vsCommandExecOption.vsCommandExecOptionDoDefault)
			{
				if(commandName == "NugetFix.Connect.NugetFix")
				{
				    _nugetFixInternal.RunNugetine();
					handled = true;
				}
			}
		}
        // ReSharper restore RedundantAssignment
	}
}