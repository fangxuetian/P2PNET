﻿using P2PNET.FileLayer.EventArgs;
using P2PNET.ObjectLayer;
using P2PNET.ObjectLayer.EventArgs;
using P2PNET.TransportLayer.EventArgs;
using PCLStorage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace P2PNET.FileLayer
{
    /// <summary>
    /// Class for sending and receiving files between peers.
    /// Built on top of <C>ObjectManager</C>
    /// </summary>
    public class FileManager
    {
        /// <summary>
        /// triggered when a file part has been sent or received sucessfully.
        /// </summary>
        public event EventHandler<FileTransferEventArgs> FileProgUpdate;

        /// <summary>
        /// Triggered when a file has been 100% received.
        /// </summary>
        public event EventHandler<FileReceivedEventArgs> FileReceived;

        /// <summary>
        /// Triggered when a new peer is detected or an existing peer becomes inactive
        /// </summary>
        public event EventHandler<PeerChangeEventArgs> PeerChange;

        /// <summary>
        /// Triggered when a message containing an object has been received
        /// </summary>
        public event EventHandler<ObjReceivedEventArgs> ObjReceived;

        private ObjectManager objManager;
        private IFileSystem fileSystem;
        private List<FileReceived> receivedFiles;
        private List<FileSent> sentFiles;
        private TaskCompletionSource<bool> stillProcPrevMsg;

        /// <summary>
        /// Constructor that instantiates a file manager. To commence listening call the method <C>StartAsync</C>.
        /// </summary>
        /// <param name="mPortNum"> The port number which this peer will listen on and send messages with </param>
        /// <param name="mForwardAll"> When true, all messages received trigger a MsgReceived event. This includes UDB broadcasts that are reflected back to the local peer.</param>
        public FileManager(int mPortNum = 8080, bool mForwardAll = false)
        {
            this.receivedFiles = new List<FileReceived>();
            this.sentFiles = new List<FileSent>();
            this.stillProcPrevMsg = new TaskCompletionSource<bool>();
            this.objManager = new ObjectManager(mPortNum, mForwardAll);
            this.fileSystem = FileSystem.Current;

            this.objManager.ObjReceived += ObjManager_objReceived;
            this.objManager.PeerChange += ObjManager_PeerChange;
        }

        /// <summary>
        /// Peer will start actively listening on the specified port number.
        /// </summary>
        /// <returns></returns>
        public async Task StartAsync()
        {
            await objManager.StartAsync();
        }

        //bufferSize = 10Kb chunks
        public async Task SendFileAsyncTCP(string ipAddress, string filePath, int bufferSize = 10 * 1024)
        {
            FileSent fileSend = await SetupFileStream(ipAddress, filePath, bufferSize);
            FilePartObj firstFilePart = await GetNextFilePart(fileSend);
            if(firstFilePart != null)
            {
                await objManager.SendAsyncTCP(ipAddress, firstFilePart);
            }
            //update progress
            FileProgUpdate?.Invoke(this, new FileTransferEventArgs(fileSend));
        }

        private async Task<FileSent> SetupFileStream(string ipAddress, string filePath, int bufferSize = 10 * 1024)
        {
            //get file details
            IFile file = await fileSystem.GetFileFromPathAsync(filePath);

            //TODO: check if file is already open
            Stream fileStream;
            try
            {
                fileStream = await file.OpenAsync(FileAccess.Read);
            }
            catch
            {
                //can't find file
                throw new FileNotFound("Can't access the file: " + filePath);
            }
            //store away file details and the stream
            FilePartObj filePart = new FilePartObj(file, fileStream.Length, bufferSize);
            FileSent fileSend = new FileSent(filePart, fileStream, ipAddress);
            sentFiles.Add(fileSend);
            return fileSend;
        }

        private async Task<FilePartObj> GetNextFilePart(FileSent fileSend)
        {
            int remainingParts = fileSend.RemainingFileParts();
            if (remainingParts <= 0)
            {
                //no parts left to send
                return null;
            }
            //send only the file part
            FilePartObj filePart = await fileSend.GetNextFilePart();
            return filePart;
        }

        private void ObjManager_PeerChange(object sender, PeerChangeEventArgs e)
        {
            PeerChange?.Invoke(this, e);
        }

        private async void ObjManager_objReceived(object sender, ObjReceivedEventArgs e)
        {
            BObject bObj = e.Obj;
            Metadata metadata = bObj.GetMetadata();

            string objType = bObj.GetType();
            switch (objType)
            {
                case "FilePartObj":
                    FilePartObj filePart = e.Obj.GetObject<FilePartObj>();
                    await ReceivedFilePart(filePart, metadata);
                    await SendAckBack(filePart, metadata);
                    break;
                case "AckMessage":
                    AckMessage ackMsg = e.Obj.GetObject<AckMessage>();
                    await ProcessAckMessage(ackMsg, metadata);
                    break;
                default:
                    //not part of file transfer.
                    //trigger object received event
                    ObjReceived?.Invoke(this, e);
                    break;
            }
        }

        //called when a file part is received
        private async Task ReceivedFilePart(FilePartObj filePart, Metadata metadata)
        {
            //check if file part is valid
            if (filePart == null)
            {
                throw new Exception("filePart has not been set.");
            }

            //check if is for a new file
            if( filePart.FilePartNum == 1)
            {
                //new file being received
                FileReceived newFileReceived = await NewFileInit(filePart, metadata);
                receivedFiles.Add(newFileReceived);
            }

            //find correct file to write to
            FileReceived fileReceived = GetFileReceivedFromFilePart(filePart, metadata);

            await fileReceived.WriteFilePartToFile(filePart);

            //log incoming file
            FileProgUpdate?.Invoke(this, new FileTransferEventArgs(fileReceived));

            //if last file part then close stream
            if (filePart.FilePartNum == filePart.TotalPartNum)
            {
                await fileReceived.CloseStream();
                FileReceived?.Invoke(this, new FileReceivedEventArgs());
            }
        }

        private async Task ProcessAckMessage(AckMessage ackMsg, Metadata metadata)
        {
            FileSent fileSent = GetSendFileFromAck(ackMsg, metadata);
            FilePartObj nextFilePart = await GetNextFilePart(fileSent);
            string ipAddress = fileSent.TargetIpAddress;
            if (nextFilePart != null)
            {
                await objManager.SendAsyncTCP(ipAddress, nextFilePart);
            }
            //update progress
            FileProgUpdate?.Invoke(this, new FileTransferEventArgs(fileSent));
        }

        //find a match based on remote ip, file name and file path
        private FileSent GetSendFileFromAck(AckMessage ackMsg, Metadata metadata)
        {
            //find corresponding sentFiles
            foreach (FileSent fileSent in sentFiles)
            {
                if (fileSent.TargetIpAddress == metadata.SourceIp && fileSent.FilePart.FileName == ackMsg.FileName && fileSent.FilePart.FilePath == ackMsg.FilePath)
                {
                    return fileSent;
                }   
            }
            //can't find coresponding file
            throw new FileNotFound("Recieved an Ack but can't find file in sent storage.");
        }

        private async Task SendAckBack(FilePartObj filePart, Metadata metadata)
        {
            //send message back to sender
            string targetIp = metadata.SourceIp;
            AckMessage ackMsg = new AckMessage(filePart);
            await objManager.SendAsyncTCP(targetIp, ackMsg);
        }
        
        private async Task<FileReceived> NewFileInit(FilePartObj filePart, Metadata metadata)
        {
            //create a folder to store the file
            IFolder root = await fileSystem.GetFolderFromPathAsync("./");
            if (await root.CheckExistsAsync("./temp/") == ExistenceCheckResult.NotFound)
            {
                //create folder
                await root.CreateFolderAsync("temp", CreationCollisionOption.FailIfExists);
            }
            IFolder tempFolder = await fileSystem.GetFolderFromPathAsync("./temp");

            //create the file
            IFile newFile = await tempFolder.CreateFileAsync(filePart.FileName, CreationCollisionOption.ReplaceExisting);
            Stream fileStream = await newFile.OpenAsync(FileAccess.ReadAndWrite);

            //store as a received file
            FileReceived fileReceived = new FileReceived(filePart, fileStream, metadata.SourceIp);
            return fileReceived;
        }

        private FileReceived GetFileReceivedFromFilePart(FilePartObj filePart, Metadata metadata)
        {
            foreach (FileReceived receivedFile in this.receivedFiles)
            {
                if (receivedFile.TargetIpAddress == metadata.SourceIp && receivedFile.FilePart.FileName == filePart.FileName && receivedFile.FilePart.FilePath == filePart.FilePath)
                {
                    return receivedFile;
                }
            }

            //can't find coresponding file
            throw new FileNotFound("Recieved an file part but can't find file in received storage.");
        }
    }
}