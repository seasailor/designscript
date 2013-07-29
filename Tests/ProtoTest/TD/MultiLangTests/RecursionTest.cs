﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using ProtoCore.DSASM.Mirror;
using ProtoTest.TD;
using ProtoCore.Lang.Replication;
using ProtoTestFx.TD;

namespace ProtoTest.TD.MultiLangTests
{
    public class RecursionTest
    {
        public TestFrameWork thisTest = new TestFrameWork();

        [SetUp]
        public void Setup()
        {
           
        }/*
        [Test]
        public void T01_Recursion_SimpleGlobalFunc()
        {
            String code =
@"
	def fx(){return = fy();}
def fy(){return = fx();}
m= fx();

";

            thisTest.RunScriptSource(code);
            Object v1 = new Object();
            v1 = null;
            thisTest.Verify("m", v1);
            thisTest.VerifyRuntimeWarning(RuntimeData.WarningId.kInvalidRecursion);

        }

        [Test]
        public void T02_NoRecursion_Replication()
        {
            String code =
@"
class A
	{
	fx :var;
	fb : B;
	constructor A(x : var)
	{
		fx = x;
		fb = B.B(x);	
	}
}

class B
{
	fy: var;
	constructor B(y : var)
	{
		fy = y;
	}
}

fa = A.A(1..3);
r1 = fa.fx;//1..3
r2 = fa.fb.fy;//1..3
fb = B.B(1..5);
r3 = fb.fy;//1..5

";

            thisTest.RunScriptSource(code);
            Object[] v1 = new Object[] {1,2,3};
            Object[] v2 = new Object[] { 1, 2, 3,4,5 };

            thisTest.Verify("r1", v1);
            thisTest.Verify("r2", v1);
            thisTest.Verify("r3", v2);
            

        }

       /* [Test]
        public void T03_Recursion_SimpleGlobalFunc_2()
        {
            String code =
@"
	def fx(){return = fy();}
def fy(){return = fz();}
def fz(){return = fx();}
m= fx();

";

            thisTest.RunScriptSource(code);
            Object v1 = new Object();
            v1 = null;
            thisTest.Verify("m", v1);
            thisTest.VerifyRuntimeWarning(RuntimeData.WarningId.kInvalidRecursion);

        }

         [Test]
        public void T04_Recursion_SimpleGlobalFunc_3()
        {
            String code =
@"
	def fx()
{
return = fx();
}

m = fx();

";

            thisTest.RunScriptSource(code);
            Object v1 = new Object();
            v1 = null;
            thisTest.Verify("m", v1);
            thisTest.VerifyRuntimeWarning(RuntimeData.WarningId.kInvalidRecursion);

        }

         [Test]
         public void T05_Recursion_SimpleGlobalFunc_4()
         {
             String code =
 @"
	class A{ b:B = B.B();}
    class B{a:A =A.A();}
m = A.A();

";
             //Assert.Fail("StackOverflow");
             thisTest.RunScriptSource(code);
             Object v1 = new Object();
             v1 = null;
             thisTest.Verify("m", v1);
             thisTest.VerifyRuntimeWarning(RuntimeData.WarningId.kInvalidRecursion);

         }

         [Test]
         public void T06_Recursion_InClassMember()
         {
             String code =
 @"
	class A
{
	fa : A = A.A();
	def fooa()
	{
		fa = A.A();
        return = fa.fooa();
	}

}
m = A.A();
n = m.fooa();

";
             //Assert.Fail("StackOverflow");
             thisTest.RunScriptSource(code);
             Object v1 = new Object();
             v1 = null;
             thisTest.Verify("m", v1);
             thisTest.VerifyBuildWarning(ProtoCore.BuildData.WarningID.kInvalidRecursion);

         }
        */


        [Test]
         public void Test()
         {
             String code =
 @"

c = 3.0..5.0;//3.0,4.0,5.0
";
             //Assert.Fail("StackOverflow");
             thisTest.RunScriptSource(code);
          

         }
        

        [Test]
        public void TestCallingConstructor()
        {
            string code = @"
class A
{
    x;
    y;
    constructor A(i)
    {
        y = i;
        x = (i > 0) ? A.A(i - 1) : null;
    }
}

a = A.A(3);  // a.x = null now. 
r = a.y.y.y;
";
            thisTest.VerifyRunScriptSource(code, "");
            thisTest.Verify("r", 1);
        }


    }
}