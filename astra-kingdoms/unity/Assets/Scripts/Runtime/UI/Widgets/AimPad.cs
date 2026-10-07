using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace AstraKingdoms.Client.UI
{
    /// <summary>Touch/mouse drag surface for aiming: vertical drag = pitch, horizontal drag = yaw.</summary>
    public sealed class AimPad : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        /// <summary>Raised with the pointer movement in screen pixels.</summary>
        public event Action<Vector2> Dragged;
        public event Action Released;

        public void OnPointerDown(PointerEventData eventData)
        {
        }

        public void OnDrag(PointerEventData eventData) => Dragged?.Invoke(eventData.delta);

        public void OnPointerUp(PointerEventData eventData) => Released?.Invoke();
    }
}
