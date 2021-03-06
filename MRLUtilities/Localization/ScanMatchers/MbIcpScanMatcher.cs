﻿using System;
using System.Collections.Generic;
using MRL.Commons;
using MRL.CustomMath;
using MRL.Exploration.ScanMatchers.Base;

namespace MRL.Exploration.ScanMatchers
{
    public class MbIcpScanMatcher : IScanMatchers
    {
        protected double BIG_INITIAL_ERROR = 1000000.0;
        protected int MAX_ITERATIONS = 200;

        public static bool ProjectionFilter = true;
        public static double MaxLaserRange = 7.9;
        public static double F = 0.95;
        public static double LMET = 3;
        public static double Br = 0.3; //radial window
        public static double Bw = 0.523333333; //angular window
        public static double MaxDistInter = 0.5;
        public static double AsocError = 0.1;

        public static double Min_Error = 0.0001;
        public static int IterSmoothConv = 2;

        // Some precomputations for each scan to speed up
        static double[] refdqx;
        static double[] refdqx2;
        static double[] refdqy;
        static double[] refdqy2;
        static double[] distref;
        static double[] refdqxdqy;
        static double error_k1;

        MatchResult IScanMatchers.Match(ScanObservation scan1, ScanObservation scan2, Pose2D initQ)
        {
            ChangeMBParameterToUnitFactor(scan1.Factor);

            int StartTime = 0;
            double Duration = 0;

            Vector4[] p = null;
            Vector4[] c = null;

            p = this.ToPoints(scan1, new Pose2D());
            preProcessOnRef(p);
            //Debug.DrawPoints(p, 0);

            Pose2D q = initQ;

            //Vector4[] f = this.ToPoints(scan2, q);
            //Debug.DrawPoints(f, 1);

            StartTime = Environment.TickCount;

            int NumConverged = 0;
            int NumIterations = 0;
            double error_ratio, error;
            double cosw, sinw, dtx, dty, tmp1, tmp2;

            while (NumIterations < MAX_ITERATIONS)
            {
                c = this.ToPoints(scan2, q);
                //Debug.DrawPoints(c, 2);

                bool ErrorInM = false;
                Heap assoc = CorrelatePointsMB(p, c, ref ErrorInM);

                if (ErrorInM)
                {
                    Duration = (Environment.TickCount - StartTime) / 1000.0;
                    return new MatchResult(initQ, q, NumIterations, Duration, false);
                }

                List<Correlation<Vector2>> assocFiltered = getFilteredCorrespondances(assoc);
                //Debug.DrawPointRelations(assocFiltered.ToArray());

                Pose2D q_min = getQmin(assocFiltered);
                q = IcpScanMatcher.CompoundPoses(q_min, q);


                // ------
                /* Compute the error of the associations */
                cosw = System.Math.Cos(q_min.Rotation); sinw = System.Math.Sin(q_min.Rotation);
                dtx = q_min.X; dty = q_min.Y;
                error = 0;
                for (int i = 0; i < assocFiltered.Count; i++)
                {
                    tmp1 = assocFiltered[i].Point2.X * cosw - assocFiltered[i].Point2.Y * sinw + dtx - assocFiltered[i].Point1.X; tmp1 *= tmp1;
                    tmp2 = assocFiltered[i].Point2.X * sinw + assocFiltered[i].Point2.Y * cosw + dty - assocFiltered[i].Point1.Y; tmp2 *= tmp2;
                    error = error + tmp1 + tmp2;
                }

                error_ratio = error / error_k1;

                // ----
                /* Check the exit criteria */
                /* Error ratio */
                if (System.Math.Abs(1.0 - error_ratio) <= Min_Error ||
                    (System.Math.Abs(q_min.X) < Min_Error && System.Math.Abs(q_min.Y) < Min_Error
                    && System.Math.Abs(q_min.Rotation) < Min_Error))
                {
                    NumConverged++;
                }
                else
                    NumConverged = 0;

                error_k1 = error;

                if (NumConverged > IterSmoothConv)
                {
                    //'Converged in iteration %d', k
                    Duration = (Environment.TickCount - StartTime) / 1000.0;
                    return new MatchResult(initQ, q, NumIterations, Duration, true);
                }

                NumIterations++;
            }

            Duration = (Environment.TickCount - StartTime) / 1000.0;
            return new MatchResult(initQ, q, MAX_ITERATIONS, Duration, false);
        }

        private void ChangeMBParameterToUnitFactor(double unitFactor)
        {
            MaxLaserRange *= unitFactor;
            LMET *= unitFactor;
            Br *= unitFactor; //radial window
            MaxDistInter *= unitFactor;
        }

        private Heap CorrelatePointsMB(Vector4[] p, Vector4[] c, ref bool err)
        {
            Heap correlations = new Heap();

            int l1 = p.Length;
            int l2 = c.Length;

            int L, R, Io;
            double dist;
            double cp_ass_ptX, cp_ass_ptY, cp_ass_ptD;
            double tmp_cp_indD;

            double q1x, q1y, q2x, q2y, p2x, p2y, dqx, dqy, dqpx, dqpy, qx, qy, dx, dy;
            double landaMin;
            double A, B, C, D;
            double LMET2;

            LMET2 = LMET * LMET;

            // ----
            /* Projection Filter */
            /* Eliminate the points that cannot be seen */
            /* Furthermore it orders the points with the angle */

            int cnt = 1; /* Becarefull with this filter (order) when the angles are big >90 */
            if (ProjectionFilter)
            {
                for (int i = 1; i < l2; i++)
                {
                    if (c[i].T >= c[cnt - 1].T)
                    {
                        c[cnt] = c[i];
                        cnt++;
                    }
                }
                l2 = cnt;
            }

            // ----
            /* Build the index for the windows (this is the role of the Bw parameter */
            /* The correspondences are searched between windows in both scans */
            /* Like this you speed up the algorithm */

            L = 0; R = 0; /* index of the window for ptoRef */
            Io = 0; /* index of the window for ptoNewRef */

            if (c[Io].T < p[L].T)
            {
                if (c[Io].T + Bw < p[L].T)
                {
                    while (Io < l2 - 1 && c[Io].T + Bw < p[L].T)
                    {
                        Io++;
                    }
                }
                else
                {
                    while (R < l1 - 1 && c[Io].T + Bw > p[R + 1].T)
                        R++;
                }
            }
            else
            {
                while (L < l1 - 1 && c[Io].T - Bw > p[L].T)
                    L++;
                R = L;
                while (R < l1 - 1 && c[Io].T + Bw > p[R + 1].T)
                    R++;
            }

            // ----
            /* Look for potential correspondences between the scans */
            /* Here is where we use the windows */

            cnt = 0;
            for (int i = Io; i < l2; i++)
            {

                // Keep the index of the original scan ordering
                //cp_associations[cnt].index=indexPtosNewRef[i];

                // Move the window
                while (L < l1 - 1 && c[i].T - Bw > p[L].T)
                    L = L + 1;
                while (R < l1 - 1 && c[i].T + Bw > p[R + 1].T)
                    R = R + 1;

                //cp_associations[cnt].L=L;
                //cp_associations[cnt].R=R;

                if (L == R)
                {
                    // Just one possible correspondence

                    // precompute stuff to speed up
                    qx = p[R].X; qy = p[R].Y;
                    p2x = c[i].X; p2y = c[i].Y;
                    dx = p2x - qx; dy = p2y - qy;
                    dist = dx * dx + dy * dy - (dx * qy - dy * qx) * (dx * qy - dy * qx) / (qx * qx + qy * qy + LMET2);

                    if (dist < Br)
                    {
                        //cp_associations[cnt].nx=ptosNewRef.laserC[i].x;
                        //cp_associations[cnt].ny=ptosNewRef.laserC[i].y;
                        //cp_associations[cnt].rx=ptosRef.laserC[R].x;
                        //cp_associations[cnt].ry=ptosRef.laserC[R].y;
                        //cp_associations[cnt].dist=dist;
                        correlations.Add(new Correlation<Vector2>(new Vector2(p[R]), new Vector2(c[i]), dist));
                        cnt++;
                    }
                }
                else if (L < R)
                {
                    // More possible correspondences

                    cp_ass_ptX = 0;
                    cp_ass_ptY = 0;
                    cp_ass_ptD = double.MaxValue;

                    /* Metric based Closest point rule */
                    for (int J = L + 1; J <= R; J++)
                    {

                        // Precompute stuff to speed up
                        q1x = p[J - 1].X; q1y = p[J - 1].Y;
                        q2x = p[J].X; q2y = p[J].Y;
                        p2x = c[i].X; p2y = c[i].Y;

                        dqx = refdqx[J - 1]; dqy = refdqy[J - 1];
                        dqpx = q1x - p2x; dqpy = q1y - p2y;
                        A = 1 / (p2x * p2x + p2y * p2y + LMET2);
                        B = (1 - A * p2y * p2y);
                        C = (1 - A * p2x * p2x);
                        D = A * p2x * p2y;

                        landaMin = (D * (dqx * dqpy + dqy * dqpx) + B * dqx * dqpx + C * dqy * dqpy) / (B * refdqx2[J - 1] + C * refdqy2[J - 1] + 2 * D * refdqxdqy[J - 1]);

                        if (landaMin < 0)
                        { // Out of the segment on one side
                            qx = q1x; qy = q1y;
                        }
                        else if (landaMin > 1)
                        { // Out of the segment on the other side
                            qx = q2x; qy = q2y;
                        }
                        else if (distref[J - 1] < MaxDistInter)
                        { // Within the segment and interpotation OK
                            qx = (1 - landaMin) * q1x + landaMin * q2x;
                            qy = (1 - landaMin) * q1y + landaMin * q2y;
                        }
                        else
                        { // Segment too big do not interpolate
                            if (landaMin < 0.5)
                            {
                                qx = q1x; qy = q1y;
                            }
                            else
                            {
                                qx = q2x; qy = q2y;
                            }
                        }

                        // Precompute stuff to see if we save the association
                        dx = p2x - qx;
                        dy = p2y - qy;
                        tmp_cp_indD = dx * dx + dy * dy - (dx * qy - dy * qx) * (dx * qy - dy * qx) / (qx * qx + qy * qy + LMET2);

                        // Check if the association is the best up to now
                        if (tmp_cp_indD < cp_ass_ptD)
                        {
                            cp_ass_ptX = qx;
                            cp_ass_ptY = qy;
                            cp_ass_ptD = tmp_cp_indD;
                        }
                    }

                    // Association compatible in distance (Br parameter)
                    if (cp_ass_ptD < Br)
                    {
                        //cp_associations[cnt].nx=ptosNewRef.laserC[i].x;
                        //cp_associations[cnt].ny=ptosNewRef.laserC[i].y;
                        //cp_associations[cnt].rx=cp_ass_ptX;
                        //cp_associations[cnt].ry=cp_ass_ptY;
                        //cp_associations[cnt].dist=cp_ass_ptD;
                        correlations.Add(new Correlation<Vector2>(new Vector2(cp_ass_ptX, cp_ass_ptY), new Vector2(c[i]), cp_ass_ptD));

                        cnt++;
                    }
                }
                else
                { // This cannot happen but just in case ...
                    //cp_associations[cnt].nx=ptosNewRef.laserC[i].x;
                    //cp_associations[cnt].ny=ptosNewRef.laserC[i].y;
                    //cp_associations[cnt].rx=0;
                    //cp_associations[cnt].ry=0;
                    //cp_associations[cnt].dist=params.Br;
                    correlations.Add(new Correlation<Vector2>(new Vector2(0, 0), new Vector2(c[i]), Br));
                    cnt++;
                }
            }  // End for (i=Io;i<ptosNewRef.numPuntos;i++){

            //cntAssociationsT=cnt;

            // Check if the number of associations is ok
            //err = cnt < l2 * AsocError;

            if (err)
            {
                
            }

            //for (int ic = 0; ic < l1; ic++)
            //{
            //    double minD2 = 1e10;
            //    int minIndex = -1;
            //    int Bw = 70;

            //    for (int ip = ic - Bw; ip <= ic + Bw; ip++)
            //    {
            //        if (ip < 0 || ip >= l2) continue;

            //        double d2 = getMbDistanceSqr(p[ip], c[ic]);
            //        if ((!filter1[ip] && !filter2[ic]) && (d2 < minD2))
            //        {
            //            minD2 = d2;
            //            minIndex = ip;
            //        }
            //    }

            //    if (minD2 <= Max_Dist)
            //    {
            //        //double r1 = minIndex * 0.5 * 0.0175f;
            //        //double r2 = ic * 0.5 * 0.0175f;
            //        //double r1 = System.Math.Atan2(p[minIndex].Y, p[minIndex].X);
            //        //double r2 = System.Math.Atan2(c[ic].Y, c[ic].X);

            //        //Pose2D pt = new Pose2D(0, 0, r2 - r1);
            //        //if (System.Math.Abs(pt.GetNormalizedRotation()) < Max_Rot)
            //        //if (System.Math.Abs(r2 - r1) < Max_Rot)
            //        correlations.Push(new Correlation<Vector2>(p[minIndex], c[ic], minD2));
            //    }
            //}

            return correlations;
        }

        private List<Correlation<Vector2>> getFilteredCorrespondances(Heap assoc)
        {
            List<Correlation<Vector2>> ret = new List<Correlation<Vector2>>();
            int cnew = ((int)(assoc.Count * 100 * F)) / 100;
            for (int i = 0; i < cnew; i++)
            {
                ret.Add((Correlation<Vector2>)assoc[assoc.Count - i - 1]);
            }

            return ret;
        }

        private double max(Pose2D q)
        {
            return System.Math.Max(System.Math.Max(q.X, q.Y), q.Rotation);
        }

        private Pose2D abs(Pose2D q)
        {
            return new Pose2D(System.Math.Abs(q.X), System.Math.Abs(q.Y), System.Math.Abs(q.Rotation));
        }

        private Pose2D getQmin(List<Correlation<Vector2>> assoc)
        {
            MathNet.Numerics.LinearAlgebra.Matrix A = new MathNet.Numerics.LinearAlgebra.Matrix(3, 3, 0);
            MathNet.Numerics.LinearAlgebra.Matrix b = new MathNet.Numerics.LinearAlgebra.Matrix(3, 1, 0);

            int n_assoc = assoc.Count;
            for (int k = 0; k < n_assoc; k++)
            {
                Vector2 pi = assoc[k].Point1;
                Vector2 ci = assoc[k].Point2;

                double ki = pi.X * pi.X + pi.Y * pi.Y + LMET * LMET;

                double cxpxPcypy = ci.X * pi.X + ci.Y * pi.Y;
                double cxpyMcypx = ci.X * pi.Y - ci.Y * pi.X;

                A[0, 0] = A[0, 0] + 1.0 - pi.Y * pi.Y / ki;
                A[0, 1] = A[0, 1] + pi.X * pi.Y / ki;
                A[0, 2] = A[0, 2] - ci.Y + pi.Y / ki * cxpxPcypy;
                A[1, 1] = A[1, 1] + 1.0 - pi.X * pi.X / ki;
                A[1, 2] = A[1, 2] + ci.X - pi.X / ki * cxpxPcypy;
                A[2, 2] = A[2, 2] + ci.X * ci.X + ci.Y * ci.Y - cxpxPcypy * cxpxPcypy / ki;

                b[0, 0] = b[0, 0] + ci.X - pi.X - pi.Y / ki * cxpyMcypx;
                b[1, 0] = b[1, 0] + ci.Y - pi.Y + pi.X / ki * cxpyMcypx;
                b[2, 0] = b[2, 0] + (cxpxPcypy / ki - 1.0) * cxpyMcypx;
            }

            // % Complete the A-matrix by assigning the symmetric portions of it
            A[1, 0] = A[0, 1];
            A[2, 0] = A[0, 2];
            A[2, 1] = A[1, 2];

            MathNet.Numerics.LinearAlgebra.Matrix Q_Min = -1 * (A.Inverse() * b);

            return new Pose2D(Q_Min[0, 0], Q_Min[1, 0], Q_Min[2, 0]);
        }

        private Pose2D getQmin2(List<Correlation<Vector2>> assoc)
        {
            int MAXLASERPOINTS = 361;
            int i;
            double LMETRICA2;
            double[] X1, Y1;
            double[] X2, Y2;
            double[] X2Y2, X1X2;
            double[] X1Y2, Y1X2;
            double[] Y1Y2;
            double[] K, DS;
            double[] DsD, X2DsD, Y2DsD;
            double[] Bs, BsD;

            X1 = new double[MAXLASERPOINTS];
            Y1 = new double[MAXLASERPOINTS];
            X2 = new double[MAXLASERPOINTS];
            Y2 = new double[MAXLASERPOINTS];
            X2Y2 = new double[MAXLASERPOINTS];
            X1X2 = new double[MAXLASERPOINTS];
            X1Y2 = new double[MAXLASERPOINTS];
            Y1X2 = new double[MAXLASERPOINTS];
            Y1Y2 = new double[MAXLASERPOINTS];
            K = new double[MAXLASERPOINTS];
            DS = new double[MAXLASERPOINTS];
            DsD = new double[MAXLASERPOINTS];
            X2DsD = new double[MAXLASERPOINTS];
            Y2DsD = new double[MAXLASERPOINTS];
            Bs = new double[MAXLASERPOINTS];
            BsD = new double[MAXLASERPOINTS];

            MathNet.Numerics.LinearAlgebra.Matrix matA, invMatA;
            MathNet.Numerics.LinearAlgebra.Matrix vecB, vecSol;

            matA = new MathNet.Numerics.LinearAlgebra.Matrix(3, 3, 0);
            invMatA = new MathNet.Numerics.LinearAlgebra.Matrix(3, 3, 0);

            vecB = new MathNet.Numerics.LinearAlgebra.Matrix(3, 1, 0);
            vecSol = new MathNet.Numerics.LinearAlgebra.Matrix(3, 1, 0);

            double A1, A2, A3, B1, B2, B3, C1, C2, C3, D1, D2, D3;
            A1 = 0; A2 = 0; A3 = 0; B1 = 0; B2 = 0; B3 = 0;
            C1 = 0; C2 = 0; C3 = 0; D1 = 0; D2 = 0; D3 = 0;

            LMETRICA2 = LMET * LMET;
            int cnt = assoc.Count;

            for (i = 0; i < cnt; i++)
            {
                X1[i] = assoc[i].Point2.X * assoc[i].Point2.X;
                Y1[i] = assoc[i].Point2.Y * assoc[i].Point2.Y;
                X2[i] = assoc[i].Point1.X * assoc[i].Point1.X;
                Y2[i] = assoc[i].Point1.Y * assoc[i].Point1.Y;
                X2Y2[i] = assoc[i].Point1.X * assoc[i].Point1.Y;

                X1X2[i] = assoc[i].Point2.X * assoc[i].Point1.X;
                X1Y2[i] = assoc[i].Point2.X * assoc[i].Point1.Y;
                Y1X2[i] = assoc[i].Point2.Y * assoc[i].Point1.X;
                Y1Y2[i] = assoc[i].Point2.Y * assoc[i].Point1.Y;

                K[i] = X2[i] + Y2[i] + LMETRICA2;
                DS[i] = Y1Y2[i] + X1X2[i];
                DsD[i] = DS[i] / K[i];
                X2DsD[i] = assoc[i].Point1.X * DsD[i];
                Y2DsD[i] = assoc[i].Point1.Y * DsD[i];

                Bs[i] = X1Y2[i] - Y1X2[i];
                BsD[i] = Bs[i] / K[i];

                A1 = A1 + (1 - Y2[i] / K[i]);
                B1 = B1 + X2Y2[i] / K[i];
                C1 = C1 + (-assoc[i].Point2.Y + Y2DsD[i]);
                D1 = D1 + (assoc[i].Point2.X - assoc[i].Point1.X - assoc[i].Point1.Y * BsD[i]);

                A2 = B1;
                B2 = B2 + (1 - X2[i] / K[i]);
                C2 = C2 + (assoc[i].Point2.X - X2DsD[i]);
                D2 = D2 + (assoc[i].Point2.Y - assoc[i].Point1.Y + assoc[i].Point1.X * BsD[i]);

                A3 = C1;
                B3 = C2;
                C3 = C3 + (X1[i] + Y1[i] - DS[i] * DS[i] / K[i]);
                D3 = D3 + (Bs[i] * (-1 + DsD[i]));
            }

            matA[0, 0] = A1; matA[0, 1] = B1; matA[0, 2] = C1;
            matA[1, 0] = A2; matA[1, 1] = B2; matA[1, 2] = C2;
            matA[2, 0] = A3; matA[2, 1] = B3; matA[2, 2] = C3;

            invMatA = matA.Inverse();

            vecB[0, 0] = D1; vecB[1, 0] = D2; vecB[2, 0] = D3;
            vecSol = invMatA * vecB;

            return new Pose2D(-vecSol[0, 0], -vecSol[1, 0], -vecSol[2, 0]);
        }

        private Vector4[] ToPoints(ScanObservation scan, Pose2D q)
        {
            Matrix2 R = new Matrix2(System.Math.Cos(q.Rotation), -System.Math.Sin(q.Rotation),
                                    System.Math.Sin(q.Rotation), System.Math.Cos(q.Rotation));

            List<Vector4> points = new List<Vector4>();

            Vector2 tmpC;
            Vector2 tmpP;

            int len = scan.RangeScanner.Range.Length - 1;
            for (int i = 0; i <= len; i++)
            {
                double dist = scan.RangeScanner.Range[i] * scan.Factor;
                double angle = scan.RangeScanner.RangeTheta[i];

                if (dist < MaxLaserRange && !scan.RangeScanner.RangeFilters[i])
                {
                    tmpC = new Vector2(System.Math.Cos(angle) * dist, System.Math.Sin(angle) * dist);
                    tmpC = R * tmpC;
                    tmpC.X += q.X;
                    tmpC.Y += q.Y;

                    tmpP = new Vector2(dist, System.Math.Atan2(tmpC.Y, tmpC.X));

                    points.Add(new Vector4(tmpC.X, tmpC.Y, tmpP.X, tmpP.Y));
                }
            }

            return points.ToArray();
        }

        private void preProcessOnRef(Vector4[] p)
        {
            refdqx = new double[p.Length];
            refdqx2 = new double[p.Length];
            refdqy = new double[p.Length];
            refdqy2 = new double[p.Length];
            distref = new double[p.Length];
            refdqxdqy = new double[p.Length];

            int l1 = p.Length;

            // Preprocess reference points
            for (int i = 0; i < l1 - 1; i++)
            {
                //car2pol(&ptosRef.laserC[i],&ptosRef.laserP[i]);
                refdqx[i] = p[i].X - p[i + 1].X;
                refdqy[i] = p[i].Y - p[i + 1].Y;
                refdqx2[i] = refdqx[i] * refdqx[i];
                refdqy2[i] = refdqy[i] * refdqy[i];
                distref[i] = refdqx2[i] + refdqy2[i];
                refdqxdqy[i] = refdqx[i] * refdqy[i];
            }

            error_k1 = BIG_INITIAL_ERROR;
        }

    }
}
