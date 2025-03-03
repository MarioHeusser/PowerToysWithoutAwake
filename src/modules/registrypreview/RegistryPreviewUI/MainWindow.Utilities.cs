// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation.Metadata;
using Windows.Storage;
using WinUIEx;

namespace RegistryPreview
{
    public sealed partial class MainWindow : WindowEx
    {
        /// <summary>
        /// Method that opens and processes the passed in file name; expected to be an absolute path and a first time open
        /// </summary>
        private bool OpenRegistryFile(string filename)
        {
            // clamp to prevent attempts to open a file larger than 10MB
            try
            {
                long fileLength = new System.IO.FileInfo(filename).Length;
                if (fileLength > 10485760)
                {
                    ShowMessageBox(resourceLoader.GetString("LargeRegistryFileTitle"), App.AppFilename + resourceLoader.GetString("LargeRegistryFile"), resourceLoader.GetString("OkButtonText"));
                    ChangeCursor(gridPreview, false);
                    return false;
                }
            }
            catch
            {
                // Do nothing here - a missing or invalid file will be caught below
            }

            // Disable parts of the UI that can cause trouble when loading
            ChangeCursor(gridPreview, true);
            textBox.Text = string.Empty;

            // clear the treeView and dataGrid no matter what
            treeView.RootNodes.Clear();
            ClearTable();

            // update the current window's title with the current filename
            UpdateWindowTitle(filename);

            // Load in the whole file in one call and plop it all into textBox
            FileStream fileStream = null;
            try
            {
                FileStreamOptions fileStreamOptions = new FileStreamOptions();
                fileStreamOptions.Access = FileAccess.Read;
                fileStreamOptions.Share = FileShare.ReadWrite;
                fileStreamOptions.Mode = FileMode.Open;

                fileStream = new FileStream(filename, fileStreamOptions);
                StreamReader streamReader = new StreamReader(fileStream);

                string filenameText = streamReader.ReadToEnd();
                textBox.Text = filenameText;
                streamReader.Close();
            }
            catch
            {
                // restore TextChanged handler to make for clean UI
                textBox.TextChanged += TextBox_TextChanged;

                // Reset the cursor but leave textBox disabled as no content got loaded
                ChangeCursor(gridPreview, false);
                return false;
            }
            finally
            {
                // clean up no matter what
                if (fileStream != null)
                {
                    fileStream.Dispose();
                }
            }

            // now that the file is loaded and in textBox, parse the data
            ParseRegistryFile(textBox.Text);

            // Getting here means that the entire REG file was parsed without incident
            // so select the root of the tree and celebrate
            if (treeView.RootNodes.Count > 0)
            {
                treeView.SelectedNode = treeView.RootNodes[0];
                treeView.Focus(FocusState.Programmatic);
            }

            // reset the cursor
            ChangeCursor(gridPreview, false);
            return true;
        }

        /// <summary>
        /// Method that re-opens and processes the filename the app already knows about; expected to not be a first time open
        /// </summary>
        private void RefreshRegistryFile()
        {
            // Disable parts of the UI that can cause trouble when loading
            ChangeCursor(gridPreview, true);

            // Get the current selected node so we can return focus to an existing node
            TreeViewNode currentNode = treeView.SelectedNode;

            // clear the treeView and dataGrid no matter what
            treeView.RootNodes.Clear();
            ClearTable();

            // the existing text is still in textBox so parse the data again
            ParseRegistryFile(textBox.Text);

            // check to see if there was a key in treeView before the refresh happened
            if (currentNode != null)
            {
                // since there is a valid node, get the FullPath of the key that was selected
                string selectedFullPath = ((RegistryKey)currentNode.Content).FullPath;

                // check to see if we still have the key in the new Dictionary of keys
                if (mapRegistryKeys.ContainsKey(selectedFullPath))
                {
                    // we found it! select it in the tree and pretend it was selected
                    TreeViewNode treeViewNode;
                    mapRegistryKeys.TryGetValue(selectedFullPath, out treeViewNode);
                    treeView.SelectedNode = treeViewNode;
                    TreeView_ItemInvoked(treeView, null);
                }
                else
                {
                    // we failed to find an existing node; it could have been deleted in the edit
                    if (treeView.RootNodes.Count > 0)
                    {
                        treeView.SelectedNode = treeView.RootNodes[0];
                    }
                }
            }
            else
            {
                // no node was previously selected so check for a RootNode and select it
                if (treeView.RootNodes.Count > 0)
                {
                    treeView.SelectedNode = treeView.RootNodes[0];
                }
            }

            // enable the UI
            ChangeCursor(gridPreview, false);
        }

        /// <summary>
        /// Parses the text that is passed in, which should be the same text that's in textBox
        /// </summary>
        private bool ParseRegistryFile(string filenameText)
        {
            // if this is a not-first open, clear out the Dictionary of nodes
            if (mapRegistryKeys != null)
            {
                mapRegistryKeys.Clear();
                mapRegistryKeys = null;
            }

            // set up a new dictionary
            mapRegistryKeys = new Dictionary<string, TreeViewNode>(StringComparer.InvariantCultureIgnoreCase);

            // As we'll be processing the text one line at a time, this string will be the current line
            string registryLine;

            // Brute force editing: for textBox to show Cr-Lf corrected, we need to strip out the \n's
            filenameText = filenameText.Replace("\r\n", "\r");

            // split apart all of the text in textBox, where one element in the array represents one line
            string[] registryLines = filenameText.Split("\r");
            if (registryLines.Length <= 1)
            {
                // after the split, we have no lines so get out
                ChangeCursor(gridPreview, false);
                return false;
            }

            // REG files have to start with one of two headers and it's case insensitive
            registryLine = registryLines[0];
            registryLine = registryLine.ToLowerInvariant();

            // make sure that this is a valid REG file, based on the first line of the file
            switch (registryLine)
            {
                case REGISTRYHEADER4:
                case REGISTRYHEADER5:
                    break;
                default:
                    ShowMessageBox(APPNAME, App.AppFilename + resourceLoader.GetString("InvalidRegistryFile"), resourceLoader.GetString("OkButtonText"));
                    ChangeCursor(gridPreview, false);
                    return false;
            }

            // these are used for populating the tree as we read in one line at a time
            TreeViewNode treeViewNode = null;
            RegistryValue registryValue = null;

            // start with the first element of the array
            int index = 1;
            registryLine = registryLines[index];

            while (index < registryLines.Length)
            {
                // special case for when the registryLine begins with a @ - make some tweaks and
                // let the regular processing handle the rest.
                if (registryLine.StartsWith("@=-", StringComparison.InvariantCulture))
                {
                    // REG file has a callout to delete the @ Value which won't work *but* the Registry Editor will
                    // clear the value of the @ Value instead, so it's still a valid line.
                    registryLine = registryLine.Replace("@=-", "\"(Default)\"=\"\"");
                }
                else if (registryLine.StartsWith("@=", StringComparison.InvariantCulture))
                {
                    // This is the a Value called "(Default)" so we tweak the line for the UX
                    registryLine = registryLine.Replace("@=", "\"(Default)\"=");
                }

                // continue until we have nothing left to read
                // switch logic, based off what the current line we're reading is
                if (registryLine.StartsWith("[-", StringComparison.InvariantCulture))
                {
                    // remove the - as we won't need it but it will get special treatment in the UI
                    registryLine = registryLine.Remove(1, 1);

                    // this is a key, so remove the first [ and last ]
                    registryLine = StripFirstAndLast(registryLine);

                    // do not track the result of this node, since it should have no children
                    AddTextToTree(registryLine, DELETEDKEYIMAGE);
                }
                else if (registryLine.StartsWith("[", StringComparison.InvariantCulture))
                {
                    // this is a key, so remove the first [ and last ]
                    registryLine = StripFirstAndLast(registryLine);

                    treeViewNode = AddTextToTree(registryLine, KEYIMAGE);
                }
                else if (registryLine.StartsWith("\"", StringComparison.InvariantCulture) && registryLine.EndsWith("=-", StringComparison.InvariantCulture))
                {
                    // this line deletes this value so it gets special treatment for the UI
                    registryLine = registryLine.Replace("=-", string.Empty);

                    // remove the "'s without removing all of them
                    registryLine = StripFirstAndLast(registryLine);

                    // Create a new listview item that will be used to display the delete value and store it
                    registryValue = new RegistryValue(registryLine, string.Empty, string.Empty);
                    SetValueToolTip(registryValue);

                    // store the ListViewItem, if we have a valid Key to attach to
                    if (treeViewNode != null)
                    {
                        StoreTheListValue((RegistryKey)treeViewNode.Content, registryValue);
                    }
                }
                else if (registryLine.StartsWith("\"", StringComparison.InvariantCulture))
                {
                    // this is a named value

                    // split up the name from the value by looking for the first found =
                    int equal = registryLine.IndexOf('=');
                    if ((equal < 0) || (equal > registryLine.Length - 1))
                    {
                        // something is very wrong
                        Debug.WriteLine(string.Format(CultureInfo.InvariantCulture, "SOMETHING WENT WRONG: {0}", registryLine));
                        break;
                    }

                    // set the name and the value
                    string name = registryLine.Substring(0, equal);
                    name = StripFirstAndLast(name);

                    // Clean out any escaped characters in the value, only for the preview
                    name = StripEscapedCharacters(name);

                    string value = registryLine.Substring(equal + 1);

                    // Create a new listview item that will be used to display the value
                    registryValue = new RegistryValue(name, "REG_SZ", string.Empty);

                    // if the first and last character is a " then this is a string value; get rid of the first and last "
                    if (value.StartsWith("\"", StringComparison.InvariantCulture) && value.EndsWith("\"", StringComparison.InvariantCulture))
                    {
                        value = StripFirstAndLast(value);
                    }
                    else
                    {
                        // this is an invalid value as there are no "s in the right side of the =
                        registryValue.Type = "ERROR";
                    }

                    if (value.StartsWith("dword:", StringComparison.InvariantCultureIgnoreCase))
                    {
                        registryValue.Type = "REG_DWORD";
                        value = value.Replace("dword:", string.Empty);
                    }
                    else if (value.StartsWith("hex(b):", StringComparison.InvariantCultureIgnoreCase))
                    {
                        registryValue.Type = "REG_QWORD";
                        value = value.Replace("hex(b):", string.Empty);
                    }
                    else if (value.StartsWith("hex:", StringComparison.InvariantCultureIgnoreCase))
                    {
                        registryValue.Type = "REG_BINARY";
                        value = value.Replace("hex:", string.Empty);
                    }
                    else if (value.StartsWith("hex(2):", StringComparison.InvariantCultureIgnoreCase))
                    {
                        registryValue.Type = "REG_EXPAND_SZ";
                        value = value.Replace("hex(2):", string.Empty);
                    }
                    else if (value.StartsWith("hex(7):", StringComparison.InvariantCultureIgnoreCase))
                    {
                        registryValue.Type = "REG_MULTI_SZ";
                        value = value.Replace("hex(7):", string.Empty);
                    }

                    // Parse for the case where a \ is added immediately after hex is declared
                    switch (registryValue.Type)
                    {
                        case "REG_QWORD":
                        case "REG_BINARY":
                        case "REG_EXPAND_SZ":
                        case "REG_MULTI_SZ":
                            if (value == @"\")
                            {
                                // pad the value, so the parsing below is triggered
                                value = @",\";
                            }

                            break;
                    }

                    // If the end of a decimal line ends in a \ then you have to keep
                    // reading the block as a single value!
                    while (value.EndsWith(@",\", StringComparison.InvariantCulture))
                    {
                        value = value.TrimEnd('\\');

                        // checking for a "blank" hex value so we can skip t
                        if (value == @",")
                        {
                            value = string.Empty;
                        }

                        index++;
                        if (index >= registryLines.Length)
                        {
                            ChangeCursor(gridPreview, false);
                            return false;
                        }

                        registryLine = registryLines[index];
                        registryLine = registryLine.TrimStart();
                        value += registryLine;
                    }

                    // Clean out any escaped characters in the value, only for the preview
                    value = StripEscapedCharacters(value);

                    // update the ListViewItem with this information
                    if (registryValue.Type != "ERROR")
                    {
                        registryValue.Value = value;
                    }

                    // update the ToolTip
                    SetValueToolTip(registryValue);

                    // store the ListViewItem, if we have a valid Key to attach to
                    if (treeViewNode != null)
                    {
                        StoreTheListValue((RegistryKey)treeViewNode.Content, registryValue);
                    }
                }

                // if we get here, it's not a Key (starts with [) or Value (starts with ") so it's likely waste (comments that start with ; fall out here)

                // read the next line from the REG file
                index++;

                // if we've gone too far, escape the proc!
                if (index >= registryLines.Length)
                {
                    // check to see if anything got parsed!
                    if (treeView.RootNodes.Count <= 0)
                    {
                        ShowMessageBox(APPNAME, App.AppFilename + resourceLoader.GetString("InvalidRegistryFile"), resourceLoader.GetString("OkButtonText"));
                    }

                    ChangeCursor(gridPreview, false);
                    return false;
                }

                // carry on with the next line
                registryLine = registryLines[index];
            }

            // last check, to see if anything got parsed!
            if (treeView.RootNodes.Count <= 0)
            {
                ShowMessageBox(APPNAME, App.AppFilename + resourceLoader.GetString("InvalidRegistryFile"), resourceLoader.GetString("OkButtonText"));
                ChangeCursor(gridPreview, false);
                return false;
            }

            return true;
        }

        /// <summary>
        /// We're going to store this ListViewItem in an ArrayList which will then
        /// be attached to the most recently returned TreeNode that came back from
        /// AddTextToTree.  If there's already a list there, we will use that list and
        /// add our new node to it.
        /// </summary>
        private void StoreTheListValue(RegistryKey registryKey, RegistryValue registryValue)
        {
            ArrayList arrayList = null;
            if (registryKey.Tag == null)
            {
                arrayList = new ArrayList();
            }
            else
            {
                arrayList = (ArrayList)registryKey.Tag;
            }

            arrayList.Add(registryValue);

            // shove the updated array into the Tag property
            registryKey.Tag = arrayList;
        }

        /// <summary>
        /// Adds the REG file that's being currently being viewed to the app's title bar
        /// </summary>
        private void UpdateWindowTitle(string filename)
        {
            string[] file = filename.Split('\\');
            if (file.Length > 0)
            {
                appWindow.Title = file[file.Length - 1] + " - " + APPNAME;
            }
            else
            {
                appWindow.Title = filename + " - " + APPNAME;
            }
        }

        /// <summary>
        /// No REG file was opened, so leave the app's title bar alone
        /// </summary>
        private void UpdateWindowTitle()
        {
            appWindow.Title = APPNAME;
        }

        /// <summary>
        /// Helper method that assumes everything is enabled/disabled together
        /// </summary>
        private void UpdateToolBarAndUI(bool enable)
        {
            UpdateToolBarAndUI(enable, enable, enable);
        }

        /// <summary>
        /// Enable command bar buttons and textBox.
        /// Note that writeButton and textBox all update with the same value on purpose
        /// </summary>
        private void UpdateToolBarAndUI(bool enableWrite, bool enableRefresh, bool enableEdit)
        {
            refreshButton.IsEnabled = enableRefresh;
            editButton.IsEnabled = enableEdit;
            writeButton.IsEnabled = enableWrite;
        }

        /// <summary>
        /// Helper method that creates a new TreeView node, attaches it to a parent if any, and then passes the new node back to the caller
        /// mapRegistryKeys is a collection of all of the [] lines in the file
        /// keys comes from the REG file and represents a bunch of nodes
        /// </summary>
        private TreeViewNode AddTextToTree(string keys, string image)
        {
            string[] individualKeys = keys.Split('\\');
            string fullPath = keys;
            TreeViewNode returnNewNode = null, newNode = null, previousNode = null;

            // Walk the list of keys backwards
            for (int i = individualKeys.Length - 1; i >= 0; i--)
            {
                // when a Key is marked for deletion, make sure it only sets the icon for the bottom most leaf
                if (image == DELETEDKEYIMAGE)
                {
                    if (i < individualKeys.Length - 1)
                    {
                        image = KEYIMAGE;
                    }
                    else
                    {
                        // special casing for Registry roots
                        switch (individualKeys[i])
                        {
                            case "HKEY_CLASSES_ROOT":
                            case "HKEY_CURRENT_USER":
                            case "HKEY_LOCAL_MACHINE":
                            case "HKEY_USERS":
                            case "HKEY_CURRENT_CONFIG":
                                image = KEYIMAGE;
                                break;
                        }
                    }
                }

                // First check the dictionary, and return the current node if it already exists
                if (mapRegistryKeys.ContainsKey(fullPath))
                {
                    // was a new node created?
                    if (returnNewNode == null)
                    {
                        // if no new nodes have been created, send out the node we should have already
                        mapRegistryKeys.TryGetValue(fullPath, out returnNewNode);
                    }
                    else
                    {
                        // as a new node was created, hook it up to this found parent
                        mapRegistryKeys.TryGetValue(fullPath, out newNode);
                        newNode.Children.Add(previousNode);
                    }

                    // return the new node no matter what
                    return returnNewNode;
                }

                // Since the path is not in the tree, create a new node and add it to the dictionary
                RegistryKey registryKey = new RegistryKey(individualKeys[i], fullPath, image, GetFolderToolTip(image));

                newNode = new TreeViewNode() { Content = registryKey, IsExpanded = true };
                mapRegistryKeys.Add(fullPath, newNode);

                // if this is the first new node we're creating, we need to return it to the caller
                if (previousNode == null)
                {
                    // capture the first node so it can be returned
                    returnNewNode = newNode;
                }
                else
                {
                    // The newly created node is a parent to the previously created node, as add it here.
                    newNode.Children.Add(previousNode);
                }

                // before moving onto the next node, tag the previous node and update the path
                previousNode = newNode;
                fullPath = fullPath.Replace(string.Format(CultureInfo.InvariantCulture, @"\{0}", individualKeys[i]), string.Empty);

                // One last check: if we get here, the parent of this node is not yet in the tree, so we need to add it as a RootNode
                if (i == 0)
                {
                    treeView.RootNodes.Add(newNode);
                    treeView.UpdateLayout();
                }
            }

            return returnNewNode;
        }

        /// <summary>
        /// Wrapper method that shows a simple one-button message box, parented by the main application window
        /// </summary>
        private async void ShowMessageBox(string title, string content, string closeButtonText)
        {
            ContentDialog contentDialog = new ContentDialog()
            {
                Title = title,
                Content = content,
                CloseButtonText = closeButtonText,
            };

            // Use this code to associate the dialog to the appropriate AppWindow by setting
            // the dialog's XamlRoot to the same XamlRoot as an element that is already present in the AppWindow.
            if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 8))
            {
                contentDialog.XamlRoot = this.Content.XamlRoot;
            }

            await contentDialog.ShowAsync();
        }

        /// <summary>
        /// Wrapper method that shows a Save/Don't Save/Cancel message box, parented by the main application window and shown when closing the app
        /// </summary>
        private async void HandleDirtyClosing(string title, string content, string primaryButtonText, string secondaryButtonText, string closeButtonText)
        {
            ContentDialog contentDialog = new ContentDialog()
            {
                Title = title,
                Content = content,
                PrimaryButtonText = primaryButtonText,
                SecondaryButtonText = secondaryButtonText,
                CloseButtonText = closeButtonText,
                DefaultButton = ContentDialogButton.Primary,
            };

            // Use this code to associate the dialog to the appropriate AppWindow by setting
            // the dialog's XamlRoot to the same XamlRoot as an element that is already present in the AppWindow.
            if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 8))
            {
                contentDialog.XamlRoot = this.Content.XamlRoot;
            }

            ContentDialogResult contentDialogResult = await contentDialog.ShowAsync();

            switch (contentDialogResult)
            {
                case ContentDialogResult.Primary:
                    // Save, then close
                    SaveFile();
                    break;
                case ContentDialogResult.Secondary:
                    // Don't save, and then close!
                    saveButton.IsEnabled = false;
                    break;
                default:
                    // Cancel closing!
                    return;
            }

            // if we got here, we should try to close again
            App.Current.Exit();
        }

        /// <summary>
        /// Method will open the Registry Editor or merge the current REG file into the Registry via the Editor
        /// Process will prompt for elevation if it needs it.
        /// </summary>
        private void OpenRegistryEditor(string fileMerge)
        {
            Process process = new Process();
            process.StartInfo.FileName = "regedit.exe";
            process.StartInfo.UseShellExecute = true;
            if (File.Exists(fileMerge))
            {
                // If Merge was called, pass in the filename as a param to the Editor
                process.StartInfo.Arguments = string.Format(CultureInfo.InvariantCulture, "\"{0}\"", fileMerge);
            }

            try
            {
                process.Start();
            }
            catch
            {
                ShowMessageBox(
                    resourceLoader.GetString("UACDialogTitle"),
                    resourceLoader.GetString("UACDialogError"),
                    resourceLoader.GetString("OkButtonText"));
            }
        }

        /// <summary>
        /// Utility method that clears out the GridView as there's no other way to do it.
        /// </summary>
        private void ClearTable()
        {
            if (listRegistryValues != null)
            {
                listRegistryValues.Clear();
            }

            dataGrid.ItemsSource = null;
        }

        /// <summary>
        /// Change the current app cursor at the grid level to be a wait cursor.  Sort of works, sort of doesn't, but it's a nice attempt.
        /// </summary>
        public void ChangeCursor(UIElement uiElement, bool wait)
        {
            // You can only change the Cursor if the visual tree is loaded
            if (!visualTreeReady)
            {
                return;
            }

            InputCursor cursor = InputSystemCursor.Create(wait ? InputSystemCursorShape.Wait : InputSystemCursorShape.Arrow);
            System.Type type = typeof(UIElement);
            type.InvokeMember("ProtectedCursor", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.SetProperty | BindingFlags.Instance, null, uiElement, new object[] { cursor }, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Wrapper method that saves the current file in place, using the current text in textBox.
        /// </summary>
        private void SaveFile()
        {
            ChangeCursor(gridPreview, true);

            // set up the FileStream for all writing
            FileStream fileStream = null;

            try
            {
                // attempt to open the existing file for writing
                FileStreamOptions fileStreamOptions = new FileStreamOptions();
                fileStreamOptions.Access = FileAccess.Write;
                fileStreamOptions.Share = FileShare.Write;
                fileStreamOptions.Mode = FileMode.OpenOrCreate;

                fileStream = new FileStream(App.AppFilename, fileStreamOptions);
                StreamWriter streamWriter = new StreamWriter(fileStream, System.Text.Encoding.Unicode);

                // if we get here, the file is open and writable so dump the whole contents of textBox
                string filenameText = textBox.Text;
                streamWriter.Write(filenameText);
                streamWriter.Flush();
                streamWriter.Close();

                // only change when the save is successful
                saveButton.IsEnabled = false;
            }
            catch (UnauthorizedAccessException ex)
            {
                // this exception is thrown if the file is there but marked as read only
                ShowMessageBox(
                    resourceLoader.GetString("ErrorDialogTitle"),
                    ex.Message,
                    resourceLoader.GetString("OkButtonText"));
            }
            catch
            {
                // this catch handles all other exceptions thrown when trying to write the file out
                ShowMessageBox(
                    resourceLoader.GetString("ErrorDialogTitle"),
                    resourceLoader.GetString("FileSaveError"),
                    resourceLoader.GetString("OkButtonText"));
            }
            finally
            {
                // clean up no matter what
                if (fileStream != null)
                {
                    fileStream.Dispose();
                }
            }

            // restore the cursor
            ChangeCursor(gridPreview, false);
        }

        private void OpenSettingsFile(string path, string filename)
        {
            string fileContents = string.Empty;
            string storageFile = Path.Combine(path, filename);
            if (File.Exists(storageFile))
            {
                try
                {
                    TextReader reader = new StreamReader(storageFile);
                    fileContents = reader.ReadToEnd();
                    reader.Close();
                }
                catch
                {
                    // set up default JSON blob
                    fileContents = "{ }";
                }
            }

            try
            {
                jsonSettings = Windows.Data.Json.JsonObject.Parse(fileContents);
            }
            catch
            {
                // set up default JSON blob
                fileContents = "{ }";
                jsonSettings = Windows.Data.Json.JsonObject.Parse(fileContents);
            }
        }

        /// <summary>
        /// Save the settings JSON blob out to a local file
        /// </summary>
        private async void SaveSettingsFile(string path, string filename)
        {
            StorageFolder storageFolder = null;
            StorageFile storageFile = null;
            string fileContents = string.Empty;

            try
            {
                storageFolder = await StorageFolder.GetFolderFromPathAsync(path);
            }
            catch (FileNotFoundException ex)
            {
                Debug.WriteLine(ex.Message);
                Directory.CreateDirectory(path);
                storageFolder = await StorageFolder.GetFolderFromPathAsync(path);
            }

            try
            {
                storageFile = await storageFolder.CreateFileAsync(filename, CreationCollisionOption.OpenIfExists);
            }
            catch (FileNotFoundException ex)
            {
                Debug.WriteLine(ex.Message);
                storageFile = await storageFolder.CreateFileAsync(filename);
            }

            try
            {
                fileContents = jsonSettings.Stringify();
                await Windows.Storage.FileIO.WriteTextAsync(storageFile, fileContents);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
        }

        /// <summary>
        /// Rip the first and last character off a string,
        /// checking that the string is at least 2 characters long to avoid errors
        /// </summary>
        private string StripFirstAndLast(string line)
        {
            if (line.Length > 1)
            {
                line = line.Remove(line.Length - 1, 1);
                line = line.Remove(0, 1);
            }

            return line;
        }

        /// <summary>
        /// Replace any escaped characters in the REG file with their counterparts, for the UX
        /// </summary>
        private string StripEscapedCharacters(string value)
        {
            value = value.Replace("\\\\", "\\");    // Replace \\ with \ in the UI
            value = value.Replace("\\\"", "\"");    // Replace \" with " in the UI
            return value;
        }

        /// <summary>
        /// Loads and returns a string for a given Key's image in the tree, based off the current set image
        /// </summary>
        private string GetFolderToolTip(string key)
        {
            string value = string.Empty;
            switch (key)
            {
                case DELETEDKEYIMAGE:
                    value = resourceLoader.GetString("ToolTipDeletedKey");
                    break;
                case KEYIMAGE:
                    value = resourceLoader.GetString("ToolTipAddedKey");
                    break;
            }

            return value;
        }

        /// <summary>
        /// Loads a string for a given Value's image in the grid, based off the current type and updates the RegistryValue that's passed in
        /// </summary>
        private void SetValueToolTip(RegistryValue registryValue)
        {
            string value = string.Empty;
            switch (registryValue.Type)
            {
                case "REG_SZ":
                case "REG_EXAND_SZ":
                case "REG_MULTI_SZ":
                    value = resourceLoader.GetString("ToolTipStringValue");
                    break;
                case "ERROR":
                    value = resourceLoader.GetString("ToolTipErrorValue");
                    break;
                case "":
                    value = resourceLoader.GetString("ToolTipDeletedValue");
                    break;
                default:
                    value = resourceLoader.GetString("ToolTipBinaryValue");
                    break;
            }

            registryValue.ToolTipText = value;
        }
    }
}
