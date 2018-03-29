using System.Collections.Generic;
using System.Linq;
using MujocoUnity;
using UnityEngine;

namespace MlaMujocoUnity {
    public class MujocoAgent : Agent 
    {
        Rigidbody rBody;
		public TextAsset MujocoXml;
        public string ActorId;
		public float[] Low;
		public float[] High;
		float[] _observation1D;
        float[] _internalLow;
        float[] _internalHigh;
        int _jointSize = 13; // 9+4
        int _numJoints = 3; // for debug object
        int _sensorOffset; // offset in observations to where senors begin
        int _numSensors;
        int _sensorSize; // number of floats per senor
        int _observationSize; // total number of floats
        MujocoController _mujocoController;
        bool _footHitTerrain;
        bool _nonFootHitTerrain;
        List<float> _actions;
        bool _wasReset;

        int _frameSkip = 4; // number of physics frames to skip between training
        int _nSteps = 1000; // total number of training steps

        void Start () {
            rBody = GetComponent<Rigidbody>();
        }
        public void SetupMujoco()
        {
            _mujocoController = GetComponent<MujocoController>();
            _numJoints = _mujocoController.qpos.Count;
            _numSensors = _mujocoController.MujocoSensors.Count;            
            _jointSize = 2;
            _sensorSize = 1;
            _sensorOffset = _jointSize * _numJoints;
            _observationSize = _sensorOffset + (_sensorSize * _numSensors);
            _observation1D = Enumerable.Repeat<float>(0f, _observationSize).ToArray();
            Low = _internalLow = Enumerable.Repeat<float>(float.MinValue, _observationSize).ToArray();
            High = _internalHigh = Enumerable.Repeat<float>(float.MaxValue, _observationSize).ToArray();
            for (int j = 0; j < _numJoints; j++)
            {
                var offset = j * _jointSize;
                _internalLow[offset+0] = -5;//-10;
                _internalHigh[offset+0] = 5;//10;
                _internalLow[offset+1] = -5;//-500;
                _internalHigh[offset+1] = 5;//500;
                // _internalLow[offset+2] = -5;//-500;
                // _internalHigh[offset+3] = 5;//500;
            }
            for (int j = 0; j < _numSensors; j++)
            {
                var offset = _sensorOffset + (j * _sensorSize);
                _internalLow[offset+0] = 0;//-10;
                _internalHigh[offset+0] = 1;//10;
            }    
            this.brain = GameObject.Find("MujocoBrain").GetComponent<Brain>();
        }

        public int GetObservationCount()
        {
            return _observation1D.Length;
        }
        public int GetActionsCount()
        {
            return _mujocoController.MujocoJoints.Count;
        }

        public override void InitializeAgent()
        {
            agentParameters = new AgentParameters();
            agentParameters.resetOnDone = true;
            agentParameters.numberOfActionsBetweenDecisions = _frameSkip;
            agentParameters.maxStep = _nSteps * _frameSkip;
        }
     
        public override void AgentReset()
        {
            _mujocoController = GetComponent<MujocoController>();
            _mujocoController.MujocoJoints = null;
            _mujocoController.MujocoSensors = null;
            // var joints = this.GetComponentsInChildren<Joint>().ToList();
            // foreach (var item in joints)
            //     Destroy(item.gameObject);
            var rbs = this.GetComponentsInChildren<Rigidbody>().ToList();
            foreach (var item in rbs)
                DestroyImmediate(item.gameObject);
            Resources.UnloadUnusedAssets();

            var mujocoSpawner = this.GetComponent<MujocoUnity.MujocoSpawner>();
            if (mujocoSpawner != null)
                mujocoSpawner.MujocoXml = MujocoXml;
            mujocoSpawner.SpawnFromXml();
            SetupMujoco();
            _mujocoController.UpdateFromExternalComponent();
        }
        public override void CollectObservations()
        {
            _mujocoController.UpdateQFromExternalComponent();
            var joints = _mujocoController.MujocoJoints;

           for (int j = 0; j < _numJoints; j++)
            {
                var offset = j * _jointSize;
                _observation1D[offset+00] = _mujocoController.qpos[j];
                _observation1D[offset+01] = _mujocoController.qvel[j];
                // _observation1D[offset+02] = _mujocoController.qglobpos[j];
            }
            for (int j = 0; j < _numSensors; j++)
            {
                var offset = _sensorOffset + (j * _sensorSize);
                _observation1D[offset+00] = _mujocoController.OnSensor[j];
                // _observation1D[offset+00] = _mujocoController.SensorIsInTouch[j]; // try this when using nstack
                // _observation1D[offset+01] = _mujocoController.MujocoSensors[j].SiteObject.transform.position.x;
                // _observation1D[offset+02] = _mujocoController.MujocoSensors[j].SiteObject.transform.position.y;
                // _observation1D[offset+03] = _mujocoController.MujocoSensors[j].SiteObject.transform.position.z;
            }
            _observation1D = _observation1D.Select(x=> UnityEngine.Mathf.Clamp(x,-5, 5)).ToArray();
            AddVectorObs(_observation1D);
        }
        public override void AgentAction(float[] vectorAction, string textAction)
        {
            _actions = vectorAction.ToList();
            for (int i = 0; i < _mujocoController.MujocoJoints.Count; i++) {
				var inp = (float)vectorAction[i];
				MujocoController.ApplyAction(_mujocoController.MujocoJoints[i], inp);
			}
            _mujocoController.UpdateFromExternalComponent();
            
            var done = Terminate_HopperOai();

            if (!IsDone())
            {
                var reward = StepReward_OaiHopper();
                SetReward(reward);
            }
            if (done)
            {
                Done();
                var reward = -100f;
                AddReward(reward);
            }
            _footHitTerrain = false;
            _nonFootHitTerrain = false;
        }  
        float StepReward_OaiHopper()
		{
			var alive_bonus = 1f;
			//var reward = (_mujocoController.qvel[0]);
			var reward = (_mujocoController.qvel[0] / (_frameSkip*2)); 
			reward += alive_bonus;
			var effort = _actions
				.Select(x=>x*x)
				.Sum();
			reward -= (float) (1e-3 * effort);
			return reward;
		}      
        bool Terminate_HopperOai()
		{
			if (_nonFootHitTerrain)
				return true;
			if (_mujocoController.qpos == null)
				return false;
			var height = _mujocoController.qpos[1];
			var angle = Mathf.Abs(_mujocoController.qpos[2]);
			bool endOnHeight = (height < .3f);
			bool endOnAngle = (angle > (1f/180f) * (5.7296f *6));
			return endOnHeight || endOnAngle;
		}

		public void OnTerrainCollision(GameObject other, GameObject terrain) {
            if (string.Compare(terrain.name, "Terrain", true) != 0)
                return;
            
            switch (other.name.ToLowerInvariant().Trim())
            {
                case "left_foot": // oai_humanoid
                case "right_foot": // oai_humanoid
                case "right_shin1": // oai_humanoid
                case "left_shin1": // oai_humanoid
                case "foot_geom": // oai_hopper  //oai_walker2d
                case "leg_geom": // oai_hopper //oai_walker2d
                case "leg_left_geom": // oai_walker2d
                case "foot_left_geom": //oai_walker2d
                case "foot_left_joint": //oai_walker2d
                case "foot_joint": //oai_walker2d
                    _footHitTerrain = true;
                    break;
                default:
                    _nonFootHitTerrain = true;
                    break;
            }
		}            
    }
}