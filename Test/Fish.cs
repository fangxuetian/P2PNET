﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace P2PNET.Test
{
    class Fish : Pet
    {
        public Fish(string petName) : base(petName)
        {
            this.Type = AnimalType.Fish;
        }
    }
}
