﻿/*
    Copyright (C) 2014 Omega software d.o.o.

    This file is part of Rhetos.

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU Affero General Public License as
    published by the Free Software Foundation, either version 3 of the
    License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU Affero General Public License for more details.

    You should have received a copy of the GNU Affero General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Rhetos.Dom.DefaultConcepts;
using Rhetos.TestCommon;
using Rhetos.Configuration.Autofac;
using Rhetos.Utilities;
using Rhetos.Processing;
using Rhetos.Processing.DefaultCommands;
using CommonConcepts.Test.Helpers;

namespace CommonConcepts.Test
{
    [TestClass]
    public class UniqueTest
    {
        private class EntityHelper
        {
            private readonly Common.ExecutionContext _executionContext;
            private readonly Common.DomRepository _repository;

            public EntityHelper(RhetosTestContainer container)
            {
                _executionContext = container.Resolve<Common.ExecutionContext>();
                _repository = container.Resolve<Common.DomRepository>();
            }

            public void Insert(string s, int i, TestUnique.R r, Guid? id = null)
            {
                _repository.TestUnique.E.Insert(new[] { new TestUnique.E { S = s, I = i, RID = r.ID, ID = id ?? Guid.NewGuid() } });
            }

            public void InsertShouldFail(string s, int i, TestUnique.R r)
            {
                string error = null;
                try
                {
                    Insert(s, i, r);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.GetType().Name + ": " + ex.Message);
                    error = ex.Message;
                    _executionContext.EntityFrameworkContext.ClearCache();
                }
                Assert.IsNotNull(error, "Insert should have failed with an exception.");
            }

            public void Update(string s, int i, TestUnique.R r, Guid id)
            {
                _repository.TestUnique.E.Update(new[] { new TestUnique.E { S = s, I = i, RID = r.ID, ID = id } });
            }

            public void UpdateShouldFail(string s, int i, TestUnique.R r, Guid id)
            {
                string error = null;
                try
                {
                    Update(s, i, r, id);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.GetType().Name + ": " + ex.Message);
                    error = ex.Message;
                    _executionContext.EntityFrameworkContext.ClearCache();
                }
                Assert.IsNotNull(error, "Update should have failed with an exception.");
            }
        }

        [TestMethod]
        public void Insert_Index3()
        {
            using (var container = new RhetosTestContainer())
            {
                container.Resolve<ISqlExecuter>().ExecuteSql(new[]
                    {
                        "DELETE FROM TestUnique.E;",
                        "DELETE FROM TestUnique.R;"
                    });

                var repository = container.Resolve<Common.DomRepository>();
                var helper = new EntityHelper(container);

                var r1 = new TestUnique.R { S = "r1" };
                var r2 = new TestUnique.R { S = "r2" };
                repository.TestUnique.R.Insert(new[] { r1, r2 });

                helper.Insert("a", 1, r1);
                helper.Insert("b", 1, r1);
                helper.Insert("a", 2, r1);
                helper.Insert("a", 1, r2);
                helper.InsertShouldFail("a", 1, r1);
            }
        }

        [TestMethod]
        public void Update_Index3()
        {
            using (var container = new RhetosTestContainer())
            {
                container.Resolve<ISqlExecuter>().ExecuteSql(new[]
                    {
                        "DELETE FROM TestUnique.E;",
                        "DELETE FROM TestUnique.R;"
                    });

                var repository = container.Resolve<Common.DomRepository>();
                var helper = new EntityHelper(container);

                var r1 = new TestUnique.R { S = "r1" };
                var r2 = new TestUnique.R { S = "r2" };
                repository.TestUnique.R.Insert(new[] { r1, r2 });

                var id1 = Guid.NewGuid();

                helper.Insert("a", 1, r1);
                helper.Insert("b", 2, r2, id1);
                helper.Update("c", 2, r2, id1);
                helper.UpdateShouldFail("a", 1, r1, id1);
            }
        }

        //================================================================

        private class EntityHelperMultiple
        {
            private readonly Common.DomRepository _repository;

            public EntityHelperMultiple(RhetosTestContainer container, Common.DomRepository repository)
            {
                _repository = container.Resolve<Common.DomRepository>();
            }

            public void Insert(string s, int i, TestUnique.R r, bool shouldFail = false)
            {
                string error = null;
                var newItem = new TestUnique.Multi { S = s, I = i, RID = r.ID, ID = Guid.NewGuid() };
                try
                {
                    Console.WriteLine("Inserting " + s + ", " + i + " ...");
                    _repository.TestUnique.Multi.Insert(new[] { newItem });
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.GetType().Name + ": " + ex.Message);
                    error = ex.Message;
                    if (!shouldFail)
                        throw;
                }
                if (shouldFail)
                {
                    Assert.IsNotNull(error, "Insert should have failed with an exception.");
                    TestUtility.AssertContains(error, "Cannot insert duplicate key");
                }
            }
        }

        private void TestIndexMultipleInsert(string s, int i, int r, bool shouldFail = false)
        {
            using (var container = new RhetosTestContainer())
            {
                container.Resolve<ISqlExecuter>().ExecuteSql(new[]
                    {
                        "DELETE FROM TestUnique.Multi;",
                        "DELETE FROM TestUnique.R;"
                    });

                var repository = container.Resolve<Common.DomRepository>();
                var helper = new EntityHelperMultiple(container, repository);

                var r1 = new TestUnique.R { S = "r1" };
                var r2 = new TestUnique.R { S = "r2" };
                repository.TestUnique.R.Insert(new[] { r1, r2 });

                helper.Insert("a", 1, r1);
                helper.Insert(s, i, r == 1 ? r1 : r2, shouldFail);
            }
        }

        [TestMethod]
        public void Insert_IndexMultiple()
        {
            TestIndexMultipleInsert("b", 2, 1);
            TestIndexMultipleInsert("a", 3, 2);
        }

        [TestMethod]
        public void Insert_IndexMultiple_Fail()
        {
            TestIndexMultipleInsert("a", 1, 1, true);
            TestIndexMultipleInsert("b", 1, 1, true);
            TestIndexMultipleInsert("a", 1, 2, true);
            TestIndexMultipleInsert("a", 2, 1, true);
        }

        //==========================================================

        [TestMethod]
        public void UniqueConstraintInApplication()
        {
            using (var container = new RhetosTestContainer())
            {
                // If a writable ORM data structure is not Entity, the constraint check will be done in the application.

                var e = container.Resolve<GenericRepository<TestUnique.E>>();
                var le = container.Resolve<GenericRepository<TestUnique.LE>>();
                e.Delete(e.Load());
                e.Insert(
                    new TestUnique.E { I = 1, S = "aaa" },
                    new TestUnique.E { I = 2, S = "aaa" });
                Assert.AreEqual("1aaa, 2aaa", TestUtility.DumpSorted(le.Load(), item => item.I + item.S), "initial state");

                le.Insert(
                    new TestUnique.LE { I = 3, S = "bbb" },
                    new TestUnique.LE { I = 4, S = "ccc" });
                Assert.AreEqual("1aaa, 2aaa, 3bbb, 4ccc", TestUtility.DumpSorted(le.Load(), item => item.I + item.S), "inserting unique S values");

                TestUtility.ShouldFail(
                    () => le.Insert(new TestUnique.LE { I = 5, S = "aaa" }),
                    "duplicate record", "TestUnique.LE", "aaa");
            }
        }

        [TestMethod]
        public void ProcessingEngineUniqueConstraintError()
        {
            using (var container = new RhetosTestContainer())
            {
                container.AddIgnoreClaims();
                var processingEngine = container.Resolve<IProcessingEngine>();
                var saveDuplicates = new SaveEntityCommandInfo
                {
                    Entity = "TestUnique.E",
                    DataToInsert = new[]
                    {
                        new TestUnique.E { I = 123, S = "abc" },
                        new TestUnique.E { I = 123, S = "abc" },
                    }
                };
                var processingEngineResult = processingEngine.Execute(new[] { saveDuplicates });
                Assert.IsFalse(processingEngineResult.Success);
                TestUtility.AssertContains(processingEngineResult.UserMessage, "duplicate");
            }
        }
    }
}
