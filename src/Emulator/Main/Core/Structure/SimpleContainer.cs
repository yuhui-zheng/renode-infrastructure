//
// Copyright (c) 2010-2018 Antmicro
// Copyright (c) 2011-2015 Realtime Embedded
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using Antmicro.Renode.Peripherals;
using System.Collections.Generic;
using Antmicro.Renode.Exceptions;
using System.Linq;

namespace Antmicro.Renode.Core.Structure
{
    public abstract class SimpleContainerBase<T> : IPeripheralContainer<T, NumberRegistrationPoint<int>>
         where T : IPeripheral
    {
        public virtual IEnumerable<NumberRegistrationPoint<int>> GetRegistrationPoints(T peripheral)
        {
            return ChildCollection.Keys.Select(x => new NumberRegistrationPoint<int>(x)).ToList();
        }

        public virtual IEnumerable<IRegistered<T, NumberRegistrationPoint<int>>> Children
        {
            get
            {
                return ChildCollection.Select(x => Registered.Create(x.Value, new NumberRegistrationPoint<int>(x.Key))).ToList();
            }
        }

        public virtual void Register(T peripheral, NumberRegistrationPoint<int> registrationPoint)
        {
            if(ChildCollection.ContainsKey(registrationPoint.Address))
            {
                throw new RegistrationException("The specified registration point is already in use.");
            }
            ChildCollection.Add(registrationPoint.Address, peripheral);
        }

        public virtual void Unregister(T peripheral)
        {
            var toRemove = ChildCollection.Where(x => x.Value.Equals(peripheral)).Select(x => x.Key).ToList();
            if(toRemove.Count == 0)
            {
                throw new RegistrationException("The specified peripheral was never registered.");
            }
            foreach(var key in toRemove)
            {
                ChildCollection.Remove(key);
            }
        }

        protected T GetByAddress(int address)
        {
            T peripheral;
            if(!TryGetByAddress(address, out peripheral))
            {
                throw new KeyNotFoundException();
            }
            return peripheral;
        }

        protected bool TryGetByAddress(int address, out T peripheral)
        {
            return ChildCollection.TryGetValue(address, out peripheral);
        }

        protected SimpleContainerBase()
        {
            ChildCollection =  new Dictionary<int, T>();
        }

        protected Dictionary<int, T> ChildCollection;
    }

    public abstract class SimpleContainer<T> : SimpleContainerBase<T>, IPeripheral
         where T : IPeripheral
    {
        public abstract void Reset();

        public override void Register(T peripheral, NumberRegistrationPoint<int> registrationPoint)
        {
            base.Register(peripheral, registrationPoint);
            machine.RegisterAsAChildOf(this, peripheral, registrationPoint);
        }

        public override void Unregister(T peripheral)
        {
            base.Unregister(peripheral);
            machine.UnregisterAsAChildOf(this, peripheral);
        }

        protected SimpleContainer(Machine machine) : base()
        {
            this.machine = machine;
        }

        protected readonly Machine machine;
    }
}

