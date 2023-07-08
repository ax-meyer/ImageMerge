using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using MessageBox.Avalonia;
using MessageBox.Avalonia.Enums;
using Prism.Commands;
using Prism.Mvvm;
using ProgressDialog;
using ProgressDialog.Avalonia;
using Icon = MessageBox.Avalonia.Enums.Icon;
using Image = NetVips.Image;

namespace ImageMerge
{
    internal class ViewModel : BindableBase
    {
        private string[] _fileList =
            { "Select file(s) through button above or paste a list of files here.", "One file per line." };

        private string _savePath = "";
        private bool _dividerLine;

        private Window _mainWindowHandle;

        public string FileList
        {
            get
            {
                string stringList = "";
                foreach (string file in _fileList)
                {
                    stringList += file + "\n";
                }

                return stringList;
            }
            set
            {
                _fileList = value.Split("\n");
                RaisePropertyChanged();
            }
        }

        public string SavePath
        {
            get => _savePath;
            set
            {
                _savePath = value;
                RaisePropertyChanged();
            }
        }

        public bool DividerLine
        {
            get => _dividerLine;
            set
            {
                _dividerLine = value;
                RaisePropertyChanged();
            }
        }

        public DelegateCommand SelectSourceCommand => new(SelectSource);
        public DelegateCommand SelectTargetCommand => new(SelectTarget);
        public DelegateCommand ConvertCommand => new(ConvertFilesProgressWrapper);

        public ViewModel(Window mainWindow)
        {
            _mainWindowHandle = mainWindow;
        }

        private async void SelectSource()
        {
            List<string> formats = new() { "jpg", "png", "bmp", "tiff" };

            OpenFileDialog dlg = new OpenFileDialog
            {
                AllowMultiple = true,
                Title = "Select file(s)",
                Filters = new List<FileDialogFilter>
                {
                    new() { Name = "Supported images", Extensions = formats },
                    new() { Name = "All files", Extensions = new List<string> { "*" } }
                }
            };

            // Display OpenFileDialog by calling ShowDialog method.
            var dlgResult = await dlg.ShowAsync(_mainWindowHandle);

            if (dlgResult is not null && dlgResult.Length > 0)
            {
                _fileList = dlgResult;
                RaisePropertyChanged(nameof(FileList));
            }
        }

        private async void SelectTarget()
        {
            OpenFolderDialog dlg = new();

            var dlgResult = await dlg.ShowAsync(_mainWindowHandle);

            if (dlgResult is not null && dlgResult.Trim() != string.Empty)
                SavePath = dlgResult;
        }

        private async void ConvertFilesProgressWrapper()
        {
            /// Setup <see cref="ExampleProgressStatus"/> object to propagte updates & cancel request between view and function
            IProgressStatus progressStatus = new ProgressStatus();

            /// Start the async function to run in the background.
            Task ts = Task.Run(() => ConvertFiles(progressStatus));

            /// Instantiate & open the progress bar window asynchronously.
            ProgressDialogWindow progressWindow =
                new ProgressDialogWindow("Example Progress Window", progressStatus, _mainWindowHandle);
            var progressWindowTask = progressWindow.ShowDialog(_mainWindowHandle);

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
                if (_savePath == null || _savePath.Trim() == "")
                {
                    throw new ArgumentException("Please select savepath before merging!");
                }

                foreach (string filename in _fileList)
                {
                    if (!File.Exists(filename))
                    {
                        throw new ArgumentException("The file \"" + filename + "\" does not exist!");
                    }
                }

                int processedImages = 0;

                // Update progress outside of parallel processing loop to avoid progress bar jumping back and forth
                // increase of the processed_images counter is done with atomic operations int the loop
                void UpdateProgress()
                {
                    int progress = (int)(processedImages / (double)_fileList.Length * 100);
                    progressStatus.Update(
                        "Merging image " + (processedImages + 1) + " and " +
                        (processedImages + 2) + " of " + _fileList.Length, progress);
                }

                progressStatus.Ct.ThrowIfCancellationRequested();

                //for (int j = 0; j < (_fileList.Length + 1) / 2; j++)
                Parallel.For(0, (_fileList.Length + 1) / 2, j =>
                    {
                        progressStatus.Ct.ThrowIfCancellationRequested();
                        // hack to emulate i += 2 iterator in loop
                        int i = j * 2;

                        // load first image and get bool on direction
                        var fistImgFile = _fileList[i];
                        Image firstImg = Image.NewFromFile(fistImgFile);
                        bool firstImgDirection = firstImg.Height > firstImg.Width;

                        // load second image. If odd number of images is provided and this is the end of the list, just clone the first image.
                        Image secondImg;
                        bool secondImgDirection;
                        bool singleImg;
                        if (i + 1 < _fileList.Length)
                        {
                            secondImg = Image.NewFromFile(_fileList[i + 1]);
                            secondImgDirection = secondImg.Height > secondImg.Width;
                            singleImg = false;
                        }
                        else
                        {
                            secondImgDirection = firstImgDirection;
                            secondImg = Image.NewFromFile(fistImgFile);
                            singleImg = true;
                        }

                        // if height > width for any image, rotate 90 degrees --> width is always the larger dimension for all later processing steps.
                        if (firstImgDirection)
                            firstImg = firstImg.Rot90();

                        if (secondImgDirection)
                            secondImg = secondImg.Rot90();


                        // scale both images to have the same width
                        if (firstImg.Width != secondImg.Width)
                        {
                            // make sure first images has the bigger width
                            if (firstImg.Width < secondImg.Width)
                                (firstImg, secondImg) = (secondImg, firstImg);

                            double factor = firstImg.Width / (double)secondImg.Width;
                            secondImg = secondImg.Resize(factor);
                        }


                        // calculate height for combined image, with divider line if requested
                        int fullHeight = firstImg.Height + secondImg.Height;
                        int dividerLineHeight = 0;
                        if (DividerLine)
                        {
                            dividerLineHeight = (int)(fullHeight * 0.01);
                            fullHeight += dividerLineHeight;
                        }

                        var fullSize = firstImg.Width * fullHeight * firstImg.Bands;
                        var fullSizeHalf = fullSize / 2;
                        // create empty new image
                        var fullImg =
                            Image.NewFromMemory(
                                Enumerable.Repeat((byte)255, fullSize).ToArray(), firstImg.Width, fullHeight,
                                firstImg.Bands, firstImg.Format);

                        // create areas where the images will be copied into the combined image
                        Rectangle firstRect = new(0, 0, firstImg.Width, firstImg.Height);
                        Rectangle secondRect = new(0, firstImg.Height + dividerLineHeight,
                            secondImg.Width,
                            secondImg.Height);

                        //var test = firstImg.WriteToMemory();

                        fullImg = fullImg.Insert(firstImg, firstRect.Left, firstRect.Top);
                        fullImg = fullImg.Insert(secondImg, secondRect.Left, secondRect.Top);

                        // save combined image
                        fullImg.WriteToFile($"{_savePath}/{i / 2}.jpg");

                        // dispose of everything
                        firstImg.Dispose();
                        secondImg.Dispose();
                        fullImg.Dispose();

                        progressStatus.Ct.ThrowIfCancellationRequested();

                        // update progressbar
                        processedImages += singleImg ? 1 : 2;
                        UpdateProgress();
                    }
                );
                progressStatus.IsFinished = true;
            }
            catch (Exception ex)
            {
                var box = MessageBoxManager.GetMessageBoxStandardWindow("Critical error!",
                    "An Error occured: " + ex.Message, ButtonEnum.Ok, Icon.Error);
            }
        }
    }
}