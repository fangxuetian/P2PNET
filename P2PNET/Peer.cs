﻿using System;
using System.Threading.Tasks;
using System.IO;
using Sockets.Plugin.Abstractions;
using P2PNET.EventArgs;
using System.Text;

namespace P2PNET
{
    public class Peer
    {
        public event EventHandler<MsgReceivedEventArgs> MsgReceived;

        public string IpAddress
        {
            get
            {
                return socketClient.RemoteAddress;
            }
        }

        private ITcpSocketClient socketClient;
        private BinaryWriter writeStream;
        private BinaryReader readStream;

        //constructor
        public Peer(ITcpSocketClient mSocketClient)
        {
            this.socketClient = mSocketClient;
            this.writeStream = new BinaryWriter(socketClient.WriteStream, Encoding.Unicode);
            this.readStream = new BinaryReader(socketClient.ReadStream, Encoding.Unicode);

            StartListening();
        }

        //deconstructor
        ~Peer()
        {
            this.socketClient.DisconnectAsync().Wait();
        }

        public async Task SendMsgTCPAsync(byte[] msg)
        {
            this.readStream.ReadString
            this.writeStream.Write()

            if (!outputStream.CanWrite)
            {
                throw (new StreamCannotWrite("Cannot send message to peer because stream is not writable"));
            }

            outputStream.Write(msg, 0, msg.Length);
            await outputStream.FlushAsync();
        }

        private async void StartListening()
        {
            string peerIp = socketClient.RemoteAddress;
            Stream inputStream = socketClient.ReadStream;
            while (true)
            {
                Byte[] buffer = new Byte[5];
                await inputStream.ReadAsync(buffer, 0, 1);
                MsgReceived?.Invoke(this, new MsgReceivedEventArgs(peerIp, buffer, TransportType.TCP));
            }
            
        }
        
    }
}