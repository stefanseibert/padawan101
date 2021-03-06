﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using UnityEngine;

// State object for receiving data from remote device.
public class StateObject
{
    // Client socket.
    public Socket workSocket = null;
    // Size of receive buffer.
    public const int BufferSize = 256;
    // Receive buffer.
    public byte[] buffer = new byte[BufferSize];
    // Received data string.
    public StringBuilder sb = new StringBuilder();

}

public class RotationServer : MonoBehaviour
{
    public int port = 25005;
    public float calibrationValue = 0;
    public Transform hand;
    private Transform phoneTransform;
    private Transform lightSaberTransform; 
    private static Quaternion gyroAttitude;
    private static Vector3 accelerationData;
    private Quaternion realRotation;
    private static Socket connectSocket;
    private static RotationServer server;
    private static string IP;
    private static bool listening;
    private static int ITEM_SIZE = 17;
    private Queue<Quaternion> rotationValues;
    private static float initialYAngle = 0f;
    private static float appliedGyroYAngle = 0f;
    private static float calibrationYAngle = 0f;

    private MainEngine engine;
    private MainEngine.Device device = MainEngine.Device.Android;


    void Awake()
    {
        IP = Network.player.ipAddress;
        rotationValues = new Queue<Quaternion>();
    }

    void Start()
    {
        GameObject realPhone = GameObject.Find("PhoneData_RotationServer");

        // Mode for the real game where no demo assets exist
        if(realPhone == null)
        {
            GameObject phone = new GameObject("PhoneData_RotationServer");
            phoneTransform = phone.transform;
            GameObject ls = new GameObject("LightSaberData_RotationServer");
            ls.transform.eulerAngles = new Vector3(0f, 0f, -90f);
            ls.transform.parent = phoneTransform;
            lightSaberTransform = ls.transform;
        }
        // Mode for the demo game where demo assets exist
        else
        {
            phoneTransform = realPhone.GetComponent<Transform>();
            lightSaberTransform = GameObject.Find("LightSaberData_RotationServer").transform;
        }
        accelerationData = Vector3.zero;
        gyroAttitude = Quaternion.identity;
        realRotation = Quaternion.identity;
        initialYAngle = transform.eulerAngles.y;
        listening = false;
        StartListening();
        server = this;
    }


    void Update()
    {
        if(engine == null)
        {
            GameObject engineObj = GameObject.Find("__MainEngine");
            if(engineObj != null)
            {
                engine = engineObj.GetComponent<MainEngine>();
            }
        }
        else
        {
            device = engine.UsedDevice();
        }
        phoneTransform.rotation = gyroAttitude;
        phoneTransform.Rotate( 0f, 0f, 180f, Space.Self ); // Swap "handedness" of quaternion from gyro.
        
        if(device == MainEngine.Device.Android)
            phoneTransform.Rotate( 90f, 245f + calibrationValue, 0f, Space.World ); // Rotate to make sense as a camera pointing out the back of your device.
        
        else if(device == MainEngine.Device.iPhone)
            phoneTransform.Rotate( 90f, 180f + calibrationValue, 0f, Space.World ); // Rotate to make sense as a camera pointing out the back of your device.
        
        appliedGyroYAngle = transform.eulerAngles.y;
        phoneTransform.Rotate( 0f, -calibrationYAngle, 0f, Space.World ); // Rotates y angle back however much it deviated when calibrationYAngle was saved.

        realRotation = getSmoothRotation(lightSaberTransform.rotation);

        if(hand != null)
        {
            hand.rotation = realRotation;
            hand.Rotate(new Vector3(180.0f, 0.0f, 0.0f), Space.Self);
        }

        if(!listening)
        {
            StartListening();
        }
    }

    // Thread signal.
    public static ManualResetEvent allDone = new ManualResetEvent(false);


    public void CalibrateYAngle()
    {
        calibrationYAngle = appliedGyroYAngle - initialYAngle; // Offsets the y angle in case it wasn't 0 at edit time.
    }

    public void StartListening()
    {

        IPAddress ipAdress = IPAddress.Parse(IP);
        IPEndPoint localEndPoint = new IPEndPoint(ipAdress, port);

        // Create a TCP/IP socket.
        connectSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp );

        // Bind the socket to the local endpoint and listen for incoming connections.
        try
        {   
            connectSocket.Bind(localEndPoint);
            connectSocket.Listen(2);

                // Start an asynchronous socket to listen for connections.
                Debug.Log("[ROTATION_SERVER] Waiting for a connection...");
                connectSocket.BeginAccept(new AsyncCallback(AcceptCallback), connectSocket );
                listening = true;
        }
        catch (Exception e)
        {
            Debug.Log("[ROTATION_SERVER] Network Exception, trying to reconnect. " + e);
            listening = false;
        }
    }

    public static void AcceptCallback(IAsyncResult ar)
    {
        // Signal the main thread to continue.
        allDone.Set();

        // Get the socket that handles the client request.
        connectSocket = (Socket) ar.AsyncState;
        connectSocket = connectSocket.EndAccept(ar);

        // Create the state object.
        StateObject state = new StateObject();
        state.workSocket = connectSocket;
        connectSocket.BeginReceive( state.buffer, 0, StateObject.BufferSize, 0, new AsyncCallback(ReadCallback), state);
        Send(connectSocket, "Hello!");
        Debug.Log("[ROTATION_SERVER] Sent INIT sequence to client.");

    }

    public static void ReadCallback(IAsyncResult ar)
    {
        String content = String.Empty;
        // Retrieve the state object and the handler socket
        // from the asynchronous state object.
        StateObject state = (StateObject) ar.AsyncState;
        Socket handler = state.workSocket;

        try
        {
            // Read data from the client socket. 
            int bytesRead = handler.EndReceive(ar);

            if (bytesRead > 0)
            {
                // There  might be more data, so store the data received so far.
                state.sb.Append(Encoding.ASCII.GetString(state.buffer,0,bytesRead));

                // Check for end-of-file tag. If it is not there, read 
                // more data.
                content = state.sb.ToString();
                if (content.IndexOf("<EOF>") > -1)
                {
                    // One block of data has been read from the client. Display it on the console.
                    //Debug.Log("[ROTATION_SERVER] Read " + content.Length  + " bytes from socket. Data : " + content + "\n");
                    getDataFromString(content);
                    bytesRead = 0;
                    state.sb.Length = 0;
                }

                handler.BeginReceive(state.buffer, 0, StateObject.BufferSize, 0, new AsyncCallback(ReadCallback), state);
            }
            // we received Zero, client disconnected
            else if(bytesRead == 0)
            {
                server.closeConnection();
            }
        }
        catch(Exception e)
        {
            Debug.Log("[ROTATION_SERVER] Network Exception, trying to reconnect. " + e);
            server.closeConnection();
        }
    }

    private Quaternion getSmoothRotation(Quaternion rot)
    {
        rotationValues.Enqueue(rot);

        if(rotationValues.Count < 4)
        {
            return rot;
        }
        else
        {   
            Quaternion[] values = new Quaternion[4];
            values = rotationValues.ToArray();
            rotationValues.Dequeue();
            Quaternion smooth1 = Quaternion.Lerp(Quaternion.Lerp(values[0], values[1], 0.5f), Quaternion.Lerp(values[1], values[2], 0.5f), 0.5f);
            Quaternion smooth2 = Quaternion.Lerp(Quaternion.Lerp(values[1], values[2], 0.5f), Quaternion.Lerp(values[2], values[3], 0.5f), 0.5f);
            Quaternion smoothed = Quaternion.Lerp(smooth1, smooth2, 0.5f);
            return smoothed;
        }
    }

    private void closeConnection()
    {
        Debug.Log("[ROTATION_SERVER] Closing Connection");

        if(connectSocket != null)
        {
            connectSocket.Close();
            connectSocket = null;
        }

        listening = false;
    }

    public Quaternion GetRotation()
    {
        return realRotation;
    }

    public Vector3 GetPosition()
    {
        if(hand != null)
        {
            return hand.position;
        }
        else
        {
            return Vector3.zero;
        }
    }

    public float GetAcceleration()
    {
        return accelerationData.magnitude;
    }

    public void ChangeCalibration(float value)
    {
        calibrationValue += value;
    }

    private static void getDataFromString(string str)
    {
        string[] temp1 = str.Split('>');
        string[] temp2 = temp1[1].Split('<');
        string vectors = temp2[0];
        string[] datas = vectors.Split(',');
        
        //double magnetTime = 0.0;
        Quaternion gyroAttitudeData = Quaternion.identity;
        Vector3 gyroRotationData = Vector3.zero;
        Vector3 accelData = Vector3.zero;
        Vector3 gravityData = Vector3.zero;
        Vector3 magnetData = Vector3.zero;

        if(datas.Length == ITEM_SIZE)
        {
            //magnetTime = double.Parse(datas[0]);

            gyroAttitudeData.x = float.Parse(datas[1]);
            gyroAttitudeData.y = float.Parse(datas[2]);
            gyroAttitudeData.z = float.Parse(datas[3]);
            gyroAttitudeData.w = float.Parse(datas[4]);

            gyroRotationData.x = float.Parse(datas[5]);
            gyroRotationData.y = float.Parse(datas[6]);
            gyroRotationData.z = float.Parse(datas[7]);

            accelData.x = float.Parse(datas[8]);
            accelData.y = float.Parse(datas[9]);
            accelData.z = float.Parse(datas[10]);

            gravityData.x = float.Parse(datas[11]);
            gravityData.y = float.Parse(datas[12]);
            gravityData.z = float.Parse(datas[13]);

            magnetData.x = float.Parse(datas[14]);
            magnetData.y = float.Parse(datas[15]);
            magnetData.z = float.Parse(datas[16]);
        }
        
        if(gyroAttitudeData != Quaternion.identity)
        {
            gyroAttitude = gyroAttitudeData;
        }
        accelerationData = accelData;
    }

    private static void Send(Socket handler, String data)
    {
        try
        {
            // Convert the string data to byte data using ASCII encoding.
            byte[] byteData = Encoding.ASCII.GetBytes(data);

            // Begin sending the data to the remote device.
            handler.BeginSend(byteData, 0, byteData.Length, 0, new AsyncCallback(SendCallback), handler);
        }
        catch (Exception e)
        {
            Debug.Log("[ROTATION_SERVER] Network Exception, trying to reconnect. " + e);
            //server.closeConnection();
        }
    }

    public static void VibratePhone()
    {
        if(listening)
        {
            Send(connectSocket, "vibration");
        }
    }

    private static void SendCallback(IAsyncResult ar)
    {
        try
        {
            // Retrieve the socket from the state object.
            Socket handler = (Socket) ar.AsyncState;
            // Complete sending the data to the remote device.
            handler.EndSend(ar);
        }
        catch (Exception e) 
        {
            Debug.Log("[ROTATION_SERVER] Network Exception, trying to reconnect. " + e);
            server.closeConnection();
        }
    }
}