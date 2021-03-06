﻿/*
* MIT License
*
* Copyright (c) 2017 Philip Tibom, Jonathan Jansson, Rickard Laurenius,
* Tobias Alldén, Martin Chemander, Sherry Davar
*
* Permission is hereby granted, free of charge, to any person obtaining a copy
* of this software and associated documentation files (the "Software"), to deal
* in the Software without restriction, including without limitation the rights
* to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
* copies of the Software, and to permit persons to whom the Software is
* furnished to do so, subject to the following conditions:
*
* The above copyright notice and this permission notice shall be included in all
* copies or substantial portions of the Software.
*
* THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
* IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
* FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
* AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
* LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
* OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
* SOFTWARE.
*/

using System.Collections.Generic;
using UnityEngine;

#pragma warning disable 0067, 0414, 0219

public struct VelodynePointCloudVertex
{
    public Vector3 position;
    public Color color;
    public System.UInt16 ringNumber;
    public float distance;
}

/// <summary>
/// Author: Philip Tibom
/// Simulates the lidar sensor by using ray casting.
/// </summary>
public class LidarSensor : MonoBehaviour, Ros.IRosClient
{
    public ROSTargetEnvironment targetEnv;
    private float lastUpdate = 0;

    private List<Laser> lasers;
    private float horizontalAngle = 0;

    public int numberOfLasers = 2;
    public float rotationSpeedHz = 1.0f;
    public float rotationAnglePerStep = 45.0f;
    public float publishInterval = 1f;
    private float publishTimeStamp;
    public float rayDistance = 100f;
    public float upperFOV = 30f;
    public float lowerFOV = 30f;
    public float offset = 0.001f;
    public float upperNormal = 30f;
    public float lowerNormal = 30f;
    List<VelodynePointCloudVertex> pointCloud;

    public float lapTime = 0;

    private bool isPlaying = false;

    public GameObject pointCloudObject;
    private float previousUpdate;

    private float lastLapTime;

    public GameObject lineDrawerPrefab;

    public string topicName = "/points_raw";
    public string ApolloTopicName = "/apollo/sensor/velodyne64/compensator/PointCloud2";

    uint seqId;
    public float exportScaleFactor = 1.0f;
    public Transform sensorLocalspaceTransform;

    public FilterShape filterShape;

    Ros.Bridge Bridge;

    // Use this for initialization
    private void Start()
    {
        publishTimeStamp = Time.fixedTime;
        lastLapTime = 0;
        pointCloud = new List<VelodynePointCloudVertex>();
        //publishInterval = 1f / rotationSpeedHz;
    }

    public void OnRosBridgeAvailable(Ros.Bridge bridge)
    {
        Bridge = bridge;
        Bridge.AddPublisher(this);
    }

    public void OnRosConnected()
    {
        if (targetEnv == ROSTargetEnvironment.AUTOWARE)
        {
            Bridge.AddPublisher<Ros.PointCloud2>(topicName);
        }

        if (targetEnv == ROSTargetEnvironment.APOLLO)
        {
            Bridge.AddPublisher<Ros.PointCloud2>(ApolloTopicName);
        }
    }

    public void Enable(bool enabled)
    {
        isPlaying = enabled;
        if (isPlaying)
        {
            InitiateLasers();
        }
        else
        {
            StopLIDAR();
        }
    }

    public void StopLIDAR()
    {
        DeleteLasers();
    }

    private void InitiateLasers()
    {
        // Initialize number of lasers, based on user selection.
        DeleteLasers();

        float upperTotalAngle = upperFOV / 2;
        float lowerTotalAngle = lowerFOV / 2;
        float upperAngle = upperFOV / (numberOfLasers / 2);
        float lowerAngle = lowerFOV / (numberOfLasers / 2);
        offset = (offset / 100) / 2; // Convert offset to centimeters.
        for (int i = 0; i < numberOfLasers; i++)
        {
            GameObject lineDrawer = Instantiate(lineDrawerPrefab);
            lineDrawer.transform.parent = gameObject.transform; // Set parent of drawer to this gameObject.
            if (i < numberOfLasers / 2)
            {
                lasers.Add(new Laser(gameObject, lowerTotalAngle + lowerNormal, rayDistance, -offset, lineDrawer, i));

                lowerTotalAngle -= lowerAngle;
            }
            else
            {
                lasers.Add(new Laser(gameObject, upperTotalAngle - upperNormal, rayDistance, offset, lineDrawer, i));
                upperTotalAngle -= upperAngle;
            }
        }
    }

    private void DeleteLasers()
    {
        if (lasers != null)
        {
            foreach (Laser l in lasers)
            {
                Destroy(l.GetRenderLine().gameObject);
            }
        }

        lasers = new List<Laser>();
    }

    public void PauseSensor(bool simulationModeOn)
    {
        if (!simulationModeOn)
        {
            isPlaying = simulationModeOn;
        }
    }

    private void FixedUpdate()
    {
        // Do nothing, if the simulator is paused.
        if (!isPlaying)
        {
            return;
        }

        // Check if number of steps is greater than possible calculations by unity.
        float numberOfStepsNeededInOneLap = 360 / Mathf.Abs(rotationAnglePerStep);
        float numberOfStepsPossible = 1 / Time.fixedDeltaTime / 5;
        float precalculateIterations = 1;
        // Check if we need to precalculate steps.
        if (numberOfStepsNeededInOneLap > numberOfStepsPossible)
        {
            precalculateIterations = (int)(numberOfStepsNeededInOneLap / numberOfStepsPossible);
            if (360 % precalculateIterations != 0)
            {
                precalculateIterations += 360 % precalculateIterations;
            }
        }

        // Check if it is time to step. Example: 2hz = 2 rotations in a second.
        if (Time.fixedTime - lastUpdate > (1f/(numberOfStepsNeededInOneLap)/rotationSpeedHz) * precalculateIterations)
        {
            // Update current execution time.
            lastUpdate = Time.fixedTime;

            for (int i = 0; i < precalculateIterations; i++)
            {
                // Perform rotation.
                transform.Rotate(0, rotationAnglePerStep, 0, Space.Self);
                horizontalAngle += rotationAnglePerStep; // Keep track of our current rotation.
                if (horizontalAngle >= 360)
                {
                    horizontalAngle -= 360;
                    lastLapTime = Time.fixedTime;
                    publishTimeStamp = Time.fixedTime;
                    SendPointCloud(pointCloud);
                    pointCloud.Clear();
                }

                // Execute lasers.
                for (int x = 0; x < lasers.Count; x++)
                {
                    RaycastHit hit = lasers[x].ShootRay();
                    float distance = hit.distance;
                    if (distance != 0 && (filterShape == null || !filterShape.Contains(hit.point))) // Didn't hit anything or in filter shape, don't add to list.
                    {
                        float verticalAngle = lasers[x].GetVerticalAngle();
                        var pcv = new VelodynePointCloudVertex();
                        pcv.position = hit.point;
                        pcv.ringNumber = (System.UInt16)x;
                        pcv.distance = distance;
                        pointCloud.Add(pcv);
                    }
                }
            }
        }
    }

    void SendPointCloud(List<VelodynePointCloudVertex> pointCloud)
    {
        if (Bridge == null || Bridge.Status != Ros.Status.Connected)
        {
            return;
        }

        var pointCount = pointCloud.Count;
        byte[] byteData = new byte[32 * pointCount];
        for (int i = 0; i < pointCount; i++)
        {
            var local = sensorLocalspaceTransform.InverseTransformPoint(pointCloud[i].position);
            local.Set(-local.x, local.y, local.z);
            local = Quaternion.Euler(90f, 0f, 0f) * local;
            local = Quaternion.Euler(0f, 0f, 90f) * local;
            var scaledPos = local * exportScaleFactor;
            var x = System.BitConverter.GetBytes(scaledPos.x);
            var y = System.BitConverter.GetBytes(scaledPos.y);
            var z = System.BitConverter.GetBytes(scaledPos.z);
            //var intensity = System.BitConverter.GetBytes(pointCloud[i].color.maxColorComponent);
            //var intensity = System.BitConverter.GetBytes((float)(((int)pointCloud[i].color.r) << 16 | ((int)pointCloud[i].color.g) << 8 | ((int)pointCloud[i].color.b)));

            //var intensity = System.BitConverter.GetBytes((byte)pointCloud[i].distance);
            var intensity = System.BitConverter.GetBytes((byte)255);

            var ring = System.BitConverter.GetBytes(pointCloud[i].ringNumber);

            var ts = System.BitConverter.GetBytes((double)0.0);

            System.Buffer.BlockCopy(x, 0, byteData, i * 32 + 0, 4);
            System.Buffer.BlockCopy(y, 0, byteData, i * 32 + 4, 4);
            System.Buffer.BlockCopy(z, 0, byteData, i * 32 + 8, 4);
            System.Buffer.BlockCopy(intensity, 0, byteData, i * 32 + 16, 1);
            System.Buffer.BlockCopy(ts, 0, byteData, i * 32 + 24, 8);
        }

        var msg = new Ros.PointCloud2()
        {
            header = new Ros.Header()
            {
                stamp = Ros.Time.Now(),
                seq = seqId++,
                frame_id = "velodyne", // needed for Autoware
            },
            height = 1,
            width = (uint)pointCount,
            fields = new Ros.PointField[] {
                new Ros.PointField()
                {
                    name = "x",
                    offset = 0,
                    datatype = 7,
                    count = 1,
                },
                new Ros.PointField()
                {
                    name = "y",
                    offset = 4,
                    datatype = 7,
                    count = 1,
                },
                new Ros.PointField()
                {
                    name = "z",
                    offset = 8,
                    datatype = 7,
                    count = 1,
                },
                new Ros.PointField()
                {
                    name = "intensity",
                    offset = 16,
                    datatype = 2,
                    count = 1,
                },
                new Ros.PointField()
                {
                    name = "timestamp",
                    offset = 24,
                    datatype = 8,
                    count = 1,
                },
            },
            is_bigendian = false,
            point_step = 32,
            row_step = (uint)pointCount * 32,
            data = byteData,
            is_dense = true,
        };

        if (targetEnv == ROSTargetEnvironment.AUTOWARE)
        {
            Bridge.Publish(topicName, msg);
        }

        if (targetEnv == ROSTargetEnvironment.APOLLO)
        {
            Bridge.Publish(ApolloTopicName, msg);
        }
    }
}
