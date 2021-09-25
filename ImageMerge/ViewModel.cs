using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Prism.Commands;
using Prism.Mvvm;
using OpenCvSharp;
using System.IO;
using ProgressDialog;
using System.Threading.Tasks;

namespace ImageMerge
{
    internal class ViewModel : BindableBase
    {
        private string[] fileList = new string[] { "Select file(s) through button above or paste a list of files here.", "One file per line." };
        private string savePath = "";
        private bool dividerLine;

        private System.Windows.Window mainWindowHandle;

        public string FileList
        {
            get
            {
                string stringList = "";
                foreach (string file in fileList)
                {
                    stringList += file + "\n";
                }
                return stringList;
            }
            set
            {
                fileList = value.Split("\n");
                RaisePropertyChanged(nameof(FileList));
            }
        }

        public string SavePath
        {
            get => savePath;
            set
            {
                savePath = value;
                RaisePropertyChanged(nameof(SavePath));
            }
        }
        public bool DividerLine
        {
            get => dividerLine;
            set
            {
                dividerLine = value;
                RaisePropertyChanged(nameof(DividerLine));
            }
        }

        public DelegateCommand SelectSourceCommand => new DelegateCommand(SelectSource);
        public DelegateCommand SelectTargetCommand => new DelegateCommand(SelectTarget);
        public DelegateCommand ConvertCommand => new DelegateCommand(ConvertFilesProgressWrapper);

        public ViewModel(System.Windows.Window mainWindow)
        {
            mainWindowHandle = mainWindow;
        }

        private void SelectSource()
        {
            List<string> formats = new List<string> { ".jpg", ".png", ".bmp", ".tiff" };
            string fileFilter = "All supported|";
            foreach (string format in formats)
            {
                if (format.ToLower().Contains("ovf"))
                {
                    continue;
                }
                fileFilter += "*" + format + ";";
            }
            foreach (string format in formats)
            {
                if (format.ToLower().Contains("ovf"))
                {
                    continue;
                }
                if (fileFilter != String.Empty)
                {
                    fileFilter += "|";
                }
                fileFilter += "*" + format + "|*" + format;
            }
            fileFilter += "|All Files|*.*";

            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = "Select file(s)",
                Filter = fileFilter,
                FilterIndex = 0
            };

            // Display OpenFileDialog by calling ShowDialog method.
            bool? dlgResult = dlg.ShowDialog();

            if (dlgResult.Value)
            {
                fileList = dlg.FileNames;
                RaisePropertyChanged(nameof(FileList));
            }
        }

        private void SelectTarget()
        {
            FolderBrowserDialog dlg = new();

            DialogResult dlgResult = dlg.ShowDialog();

            if (dlg.SelectedPath != "")
            {
                SavePath = dlg.SelectedPath;
            }
        }

        private async void ConvertFilesProgressWrapper()
        {
            /// Setup <see cref="ExampleProgressStatus"/> object to propagte updates & cancel request between view and function
            IProgressStatus progressStatus = new ProgressStatus();

            /// Start the async function to run in the background.
            Task ts = Task.Run(() => ConvertFiles(progressStatus));

            /// Instantiate & open the progress bar window asynchronously.
            ProgressDialogWindow progressWindow = new ProgressDialogWindow("Example Progress Window", progressStatus, mainWindowHandle);
            Task<bool?> progressWindowTask = progressWindow.ShowDialogAsync();

            /// Wait for the async task to finish, handle cancelation exception.
            try
            {
                await ts;
            }
            catch (OperationCanceledException)
            {
                // nothing to handle here
            }

            // close the window
            progressWindow.Close();
            await progressWindowTask;
        }

        private void ConvertFiles(IProgressStatus progressStatus)
        {
            try
            {
                if (savePath == null || savePath.Trim() == "")
                {
                    throw new ArgumentException("Please select savepath before merging!");
                }

                foreach (string filename in fileList)
                {
                    if (!File.Exists(filename))
                    {
                        throw new ArgumentException("The file \"" + filename + "\" does not exist!");
                    }
                }

                int processed_images = 0;

                // Update progress outside of parallel processing loop to avoid progress bar jumping back and forth
                // increase of the processed_images counter is done with atomic operations int the loop
                void UpdateProgress()
                {
                    int progress = (int)(processed_images / (double)fileList.Length * 100);
                    progressStatus.Update("Merging image " + (processed_images + 1).ToString() + " and " + (processed_images + 2).ToString() + " of " + fileList.Length.ToString(), progress);

                }

                progressStatus.CT.ThrowIfCancellationRequested();

                //for (int j = 0; j < (fileList.Length + 1) / 2; j++)
                Parallel.For(0, (fileList.Length + 1) / 2, j =>
                {
                    progressStatus.CT.ThrowIfCancellationRequested();
                    // hack to emulate i += 2 iterator in loop
                    int i = j * 2;

                    // load first image and get bool on direction
                    Mat firstImg = new(fileList[i]);
                    bool firstImgDirection = firstImg.Height > firstImg.Width;

                    // load second image. If odd number of images is provided and this is the end of the list, just clone the first image.
                    Mat secondImg;
                    bool secondImgDirection;
                    bool singleImg;
                    if (i + 1 < fileList.Length)
                    {
                        secondImg = new(fileList[i + 1]);
                        secondImgDirection = secondImg.Height > secondImg.Width;
                        singleImg = false;
                    }
                    else
                    {
                        secondImgDirection = firstImgDirection;
                        secondImg = firstImg.Clone();
                        singleImg = true;
                    }

                    // if height > width for any image, rotate 90 degrees --> width is always the larger dimension for all later processing steps.
                    if (firstImgDirection)
                    {
                        Cv2.Rotate(firstImg, firstImg, RotateFlags.Rotate90Clockwise);
                    }

                    if (secondImgDirection)
                    {
                        Cv2.Rotate(secondImg, secondImg, RotateFlags.Rotate90Clockwise);
                    }

                    

                    // scale both images to have the same width
                    if (firstImg.Width != secondImg.Width)
                    {
                        // make sure first images has the bigger width
                        if (firstImg.Width < secondImg.Width)
                        {
                            // without clone, its just pointers being moved around --> no dispose needed
                            Mat tmp = firstImg;
                            firstImg = secondImg;
                            secondImg = tmp;
                        }

                        double factor = firstImg.Width / (double)secondImg.Width;
                        int new_height = (int)Math.Round(secondImg.Height * factor);
                        Cv2.Resize(secondImg, secondImg, new Size(firstImg.Width, new_height));
                    }


                    // calculate height for combined image, with divider line if requested
                    int fullHeight = firstImg.Height + secondImg.Height;
                    int dividerLineHeight = 0;
                    if (DividerLine)
                    {
                        dividerLineHeight = (int)(fullHeight * 0.01);
                        fullHeight += dividerLineHeight;
                    }

                    // create empty new image
                    Mat fullImg = new(new Size(firstImg.Width, fullHeight), firstImg.Type());

                    // create areas where the images will be copied into the combined image
                    Rect firstRect = new(0, 0, firstImg.Width, firstImg.Height);
                    Rect secondRect = new(0, firstImg.Height + dividerLineHeight, secondImg.Width, secondImg.Height);
                    Mat firstSubMat = fullImg.SubMat(firstRect);
                    Mat secondSubMat = fullImg.SubMat(secondRect);

                    // copy images into combined image
                    firstImg.CopyTo(firstSubMat);
                    secondImg.CopyTo(secondSubMat);
                    
                    // save combined image
                    string savename = savePath + "\\" + (i / 2).ToString() + ".jpg";
                    Cv2.ImWrite(savename, fullImg);

                    // dispose of everything
                    firstImg.Dispose();
                    secondImg.Dispose();
                    firstSubMat.Dispose();
                    secondSubMat.Dispose();
                    fullImg.Dispose();

                    progressStatus.CT.ThrowIfCancellationRequested();

                    // update progressbar
                    processed_images += singleImg ? 1 : 2;
                    UpdateProgress();
                }
                );
                progressStatus.IsFinished = true;

            }
            catch (Exception ex)
            {
                MessageBox.Show("An Error occured: " + ex.Message, "Critical error!", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
